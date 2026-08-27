using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Brings one stack back after an instance restore (ADR-0027 §6): deploy it from git — the definition
/// arrived with the restored database — and then restore its newest archive into the volumes that deploy
/// created.
/// </summary>
/// <remarks>
/// <para>
/// The order is the whole point. A restore needs the volumes to exist, and only a deploy creates them;
/// a deploy on its own leaves the stack running with empty ones. Doing it by hand means remembering
/// that for every stack, in the middle of a disaster.
/// </para>
/// <para>
/// Progress is followed by reading each run's own event rather than by holding the work: both queues
/// already own their runs, both persist their outcome, and a coordinator that tried to own them too
/// would be a second opinion about what happened. It also means a restart mid-revival loses nothing
/// but the polling — the checklist row says what it was doing, and the operator can press it again.
/// </para>
/// </remarks>
public sealed class StackRevivalCoordinator(
    DeployQueueService deploys,
    BackupQueueService backups,
    BackupStorageFactory storageFactory,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider timeProvider,
    ILogger<StackRevivalCoordinator> logger) {
    /// <summary>How long one stack's deploy or restore may run before the revival gives up watching.</summary>
    internal static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromHours(2);

    /// <summary>Gap between reads of a running event's status.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _stepTimeout = DefaultStepTimeout;
    private readonly TimeSpan _pollInterval = DefaultPollInterval;

    /// <summary>
    /// Test seam for the wait: a test cannot spend two real hours proving the ceiling works, and a
    /// two-second poll would make every revival test that slow. Same shape as
    /// <see cref="PostgresDumpService"/>'s injected readiness wait; the parameters are not resolvable
    /// from the container, so DI keeps picking the public constructor.
    /// </summary>
    internal StackRevivalCoordinator(
        DeployQueueService deploys, BackupQueueService backups, BackupStorageFactory storageFactory,
        IServiceScopeFactory scopeFactory, IOptionsMonitor<WatchtowerOptions> options,
        TimeProvider timeProvider, ILogger<StackRevivalCoordinator> logger,
        TimeSpan stepTimeout, TimeSpan pollInterval)
        : this(deploys, backups, storageFactory, scopeFactory, options, timeProvider, logger) {
        _stepTimeout = stepTimeout;
        _pollInterval = pollInterval;
    }

    /// <summary>
    /// Revives one stack, updating its row on the checklist as it goes. Serialized process-wide: the two
    /// queues below are single-flight anyway, and running several revivals at once would only interleave
    /// their waiting.
    /// </summary>
    /// <param name="stackId">The stack to revive, as the restored database numbers it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stack's row as it ended up, or null when the checklist does not list it.</returns>
    public async Task<RevivalStack?> ReviveAsync(int stackId, CancellationToken ct) {
        await _gate.WaitAsync(ct);
        try {
            return await ReviveOneAsync(stackId, ct);
        } finally {
            _gate.Release();
        }
    }

    /// <summary>
    /// Revives every stack still pending or failed, one after another. A failure does not stop the rest:
    /// the checklist is a list of independent stacks, and stopping at the first would leave the operator
    /// to work out which of the others had been tried.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many stacks ended up done.</returns>
    public async Task<int> ReviveAllAsync(CancellationToken ct) {
        await _gate.WaitAsync(ct);
        try {
            var checklist = await LoadAsync(ct);
            if (checklist is null) return 0;

            var revived = 0;
            foreach (var stack in checklist.Stacks
                .Where(s => s.Status is RevivalStatus.Pending or RevivalStatus.Failed)
                .ToList()) {
                var result = await ReviveOneAsync(stack.StackId, ct);
                if (result?.Status == RevivalStatus.Done) revived++;
            }
            return revived;
        } finally {
            _gate.Release();
        }
    }

    private async Task<RevivalStack?> ReviveOneAsync(int stackId, CancellationToken ct) {
        var checklist = await LoadAsync(ct);
        if (checklist?.Stacks.FirstOrDefault(s => s.StackId == stackId) is not { } entry) return null;

        try {
            var deployEventId = deploys.Enqueue(stackId, DeployTriggers.Manual).DeployEventId;
            entry = await SaveAsync(
                entry with {
                    Status = RevivalStatus.Deploying, Detail = "Deploying from git…",
                    DeployEventId = deployEventId, BackupEventId = null,
                }, ct);

            var deployed = await WaitForDeployAsync(deployEventId, ct);
            if (!deployed.Success)
                return await SaveAsync(
                    entry with { Status = RevivalStatus.Failed, Detail = $"The deploy {deployed.Detail}." }, ct);

            // The archive is looked for only now: a stack whose volumes were just created has somewhere
            // to put one, and the newest archive is whatever the storage holds at this moment.
            var archive = await NewestArchiveAsync(stackId, ct);
            if (archive is null)
                return await SaveAsync(
                    entry with {
                        Status = RevivalStatus.Done,
                        Detail = "Deployed. No archive on the backup storage, so nothing was restored.",
                    }, ct);

            if (backups.TryEnqueueRestore(stackId, archive) is not { } restore)
                return await SaveAsync(
                    entry with {
                        Status = RevivalStatus.Failed,
                        Detail = "Deployed, but a backup or restore was already running for this stack.",
                    }, ct);

            entry = await SaveAsync(
                entry with {
                    Status = RevivalStatus.Restoring, Detail = $"Restoring {archive}…",
                    BackupEventId = restore.BackupEventId,
                }, ct);

            var restored = await WaitForBackupAsync(restore.BackupEventId, ct);
            return await SaveAsync(
                restored.Success
                    ? entry with { Status = RevivalStatus.Done, Detail = $"Deployed and restored from {archive}." }
                    : entry with { Status = RevivalStatus.Failed, Detail = $"The restore {restored.Detail}." },
                ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "Reviving stack {StackId} after a restore failed", stackId);
            return await SaveAsync(entry with { Status = RevivalStatus.Failed, Detail = ex.Message }, ct);
        }
    }

    /// <summary>Marks one stack as handled by the operator, so "revive all" leaves it alone.</summary>
    public async Task<RevivalStack?> SkipAsync(int stackId, CancellationToken ct) {
        var checklist = await LoadAsync(ct);
        if (checklist?.Stacks.FirstOrDefault(s => s.StackId == stackId) is not { } entry) return null;
        return await SaveAsync(
            entry with { Status = RevivalStatus.Skipped, Detail = "Skipped — handled outside Watchtower." },
            ct);
    }

    /// <summary>The checklist as it stands, or null when there is none.</summary>
    public async Task<StackRevivalState?> LoadAsync(CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        return await StackRevivalState.LoadAsync(settings, ct);
    }

    /// <summary>Puts the checklist away. It is a prompt, not a record — the audit trail is the record.</summary>
    public async Task DismissAsync(CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        if (await StackRevivalState.LoadAsync(settings, ct) is not { } checklist) return;
        await (checklist with { Dismissed = true }).SaveAsync(settings, ct);
    }

    /// <summary>
    /// Writes one row back, re-reading the checklist first so a concurrent change to another stack is
    /// not overwritten by this one's stale copy.
    /// </summary>
    private async Task<RevivalStack> SaveAsync(RevivalStack stack, CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        if (await StackRevivalState.LoadAsync(settings, ct) is { } checklist)
            await checklist.With(stack).SaveAsync(settings, ct);
        return stack;
    }

    /// <summary>The newest archive on the storage for one stack, or null when it has none.</summary>
    private async Task<string?> NewestArchiveAsync(int stackId, CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stackId, ct);
        if (stack is null) return null;

        var backup = options.CurrentValue.Backup;
        try {
            using var storage = storageFactory.Create(backup);
            var directory = BackupNaming.ResolveDirectory(stack, backup.ResolveInstanceName());
            return (await storage.ListFilesAsync(directory, ct))
                .Select(f => (f.Name, TakenAt: BackupNaming.ParseTimestamp(f.Name)))
                .Where(x => x.TakenAt is not null)
                .OrderByDescending(x => x.TakenAt)
                .Select(x => x.Name)
                .FirstOrDefault();
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Reported as "no archive" rather than as a failure: the deploy worked, which is most of the
            // value, and the operator can restore by hand from the stack's own Backups tab.
            logger.LogWarning(ex, "Could not list the backup storage while reviving stack {StackId}", stackId);
            return null;
        }
    }

    /// <summary>Waits for one deploy to reach a terminal state.</summary>
    private Task<(bool Success, string Detail)> WaitForDeployAsync(int deployEventId, CancellationToken ct) =>
        WaitAsync(async db => {
            var status = await db.DeployEvents.AsNoTracking()
                .Where(e => e.Id == deployEventId).Select(e => e.Status).FirstOrDefaultAsync(ct);
            return status;
        }, ct);

    /// <summary>Waits for one backup or restore to reach a terminal state.</summary>
    private Task<(bool Success, string Detail)> WaitForBackupAsync(int backupEventId, CancellationToken ct) =>
        WaitAsync(async db => {
            var status = await db.BackupEvents.AsNoTracking()
                .Where(e => e.Id == backupEventId).Select(e => e.Status).FirstOrDefaultAsync(ct);
            return status;
        }, ct);

    /// <summary>
    /// Polls one run's status until it stops being queued or running. Both event tables spell the four
    /// states the same way, which is what lets one loop follow either.
    /// </summary>
    private async Task<(bool Success, string Detail)> WaitAsync(
        Func<WatchtowerDbContext, Task<string?>> read, CancellationToken ct) {
        var deadline = timeProvider.GetUtcNow() + _stepTimeout;
        while (true) {
            string? status;
            await using (var scope = scopeFactory.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
                status = await read(db);
            }

            switch (status) {
                case BackupStatuses.Success:
                    return (true, "succeeded");
                case BackupStatuses.Failed:
                    return (false, "failed — its own log says why");
                case null:
                    // The row is gone: the stack was deleted under us, so there is nothing to revive.
                    return (false, "left no record — the stack no longer exists");
            }

            if (timeProvider.GetUtcNow() >= deadline)
                return (false, $"was still running after {_stepTimeout.TotalHours:0} hours");
            await Task.Delay(_pollInterval, timeProvider, ct);
        }
    }
}
