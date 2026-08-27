using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Services;

/// <summary>
/// Works out what happened to an instance restore, on the first start after the coordinator stopped and
/// started this container (ADR-0027 §5) — and repairs the few things a replayed database gets wrong
/// about the present.
/// </summary>
/// <remarks>
/// <para>
/// The verdict comes from the nonce the restore wrote into the database it was about to replace. If it
/// is gone, the replay committed: nothing else knows that value, so nothing else could have removed it.
/// If it is still there, the coordinator never replaced the database and this instance is exactly as it
/// was — which is a failure worth reporting, not a silent no-op.
/// </para>
/// <para>
/// Runs as a hosted service rather than inline in the startup path because it is only ever relevant
/// after a restore, and a restore is rare: an instance that has never had one does one file check.
/// </para>
/// </remarks>
public sealed class RestoreCompletionService(
    InstanceRestoreStaging staging,
    DockerEngineClient docker,
    ProxyChangeSignal proxySignal,
    IServiceScopeFactory scopeFactory,
    ILogger<RestoreCompletionService> logger) : IHostedService {
    /// <summary>How the last restore ended, for <c>backups.getRestoreStatus</c>.</summary>
    public RestoreOutcome LastOutcome { get; private set; } = RestoreOutcome.None;

    /// <summary>What went wrong, when <see cref="LastOutcome"/> is a failure.</summary>
    public string? LastError { get; private set; }

    /// <summary>How many lines of the coordinator's output are kept for the audit row.</summary>
    private const int CoordinatorLogTailLines = 40;

    public async Task StartAsync(CancellationToken ct) {
        if (staging.ReadProgress() is not { } progress) return;
        try {
            await CompleteAsync(progress, ct);
        } catch (Exception ex) {
            // Never allowed to stop the host coming up: an instance that will not start is strictly
            // worse than one whose restore outcome went unrecorded.
            logger.LogError(ex, "Could not complete the instance restore recorded in the staging directory");
            staging.ClearProgress();
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task CompleteAsync(RestoreProgress progress, CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();

        var pending = await settings.GetStringAsync(
            WatchtowerSettingPaths.RestorePendingNonce, SettingsScope.Global, ct);
        var replayed = !string.Equals(pending, progress.Nonce, StringComparison.Ordinal);
        var coordinatorLog = await ReadCoordinatorLogAsync(progress.CoordinatorId, ct);

        if (!replayed) {
            LastOutcome = RestoreOutcome.Failed;
            LastError = "The database was not replaced — this instance is running on the database it had.";
            logger.LogError(
                "The instance restore from '{SourceInstance}' did not complete; the database is unchanged. "
                + "Coordinator output:\n{CoordinatorLog}", progress.SourceInstance, coordinatorLog);
            await audit.RecordAsync(
                BackupService.AuditCategory, "instance.restore", InstanceRestoreService.AuditTarget,
                $"restore from '{progress.SourceInstance}' did not complete{Tail(coordinatorLog)}",
                success: false, error: LastError, ct: CancellationToken.None);
            // Our own litter, in a database that is staying: the marker row means nothing now.
            await settings.RemoveAsync(
                WatchtowerSettingPaths.RestorePendingNonce, SettingsScope.Global, expectedVersion: null, ct);
            // The upload survives, so the operator can look at the log and try again.
            staging.ClearProgress();
            return;
        }

        LastOutcome = RestoreOutcome.Succeeded;
        LastError = null;
        logger.LogWarning(
            "This instance was restored from a backup of '{SourceInstance}'; its previous database is gone",
            progress.SourceInstance);

        // ── What a replayed database is wrong about ──────────────────────────
        // The restored rows describe the source instance at the moment of its dump, so anything that
        // tracks *this* instance's present has to be corrected before it acts on stale beliefs.

        // The schedule cursors rolled back with everything else. Left alone, every window between the
        // dump and now looks missed, and the misfire grace would fire a backup of every stack at once —
        // against volumes that have not been redeployed yet.
        var now = DateTimeOffset.UtcNow;
        await db.Stacks
            .Where(s => s.LastScheduledBackupAt == null || s.LastScheduledBackupAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastScheduledBackupAt, now), ct);
        await settings.SetStringAsync(
            WatchtowerSettingPaths.BackupSelfLastScheduledAt, now.UtcDateTime.ToString("O"),
            SettingsScope.Global, expectedVersion: null, ct);

        // The routes table arrived wholesale; the proxy plane has to re-project it rather than keep
        // serving what this instance had before.
        await proxySignal.BumpAsync("instance restored from a backup bundle", ct);

        // The checklist the operator works through next: redeploy each stack, then restore its volumes.
        await StackRevivalState.SeedAsync(settings, progress, db, ct);

        await audit.RecordAsync(
            BackupService.AuditCategory, "instance.restore", InstanceRestoreService.AuditTarget,
            $"restored from a bundle taken from '{progress.SourceInstance}' "
            + $"({progress.StackNames.Count} stack(s) to revive){Tail(coordinatorLog)}",
            ct: CancellationToken.None);

        // The bundle has served its purpose, and it holds every secret the source instance had.
        staging.Clear();
        staging.ClearProgress();
    }

    /// <summary>
    /// The coordinator's output, so the audit row can say what it actually did. Best effort — it is a
    /// stopped container that may already have been reaped, and its absence must not change the verdict.
    /// </summary>
    private async Task<string?> ReadCoordinatorLogAsync(string? coordinatorId, CancellationToken ct) {
        if (coordinatorId is not { Length: > 0 }) return null;
        try {
            var lines = new List<string>();
            await foreach (var line in docker.StreamLogsAsync(
                coordinatorId, CoordinatorLogTailLines, follow: false, ct))
                lines.Add(line);
            return lines.Count == 0 ? null : string.Join(" | ", lines);
        } catch (Exception ex) {
            logger.LogDebug(ex, "Could not read the restore coordinator's log");
            return null;
        }
    }

    private static string Tail(string? coordinatorLog) =>
        coordinatorLog is { Length: > 0 } ? $" · coordinator: {coordinatorLog}" : "";
}
