using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>What a chained backup is supposed to be followed by.</summary>
public enum BackupChainKind {
    /// <summary>Deploy the stack (the pre-rollout backup of design.md §"Backups across tenants").</summary>
    Deploy,

    /// <summary>Tear the tenant down (the final backup a tenant removal offers).</summary>
    TenantTeardown,
}

/// <summary>
/// One thing to do when a chained backup finishes, and the identity it needs to do it.
/// </summary>
/// <param name="Kind">Which follow-up.</param>
/// <param name="StackId">The stack the backup was of.</param>
/// <param name="DeployTrigger">
/// For <see cref="BackupChainKind.Deploy"/>: the trigger the chained deploy is recorded under — the
/// trigger the caller <em>would</em> have used, so the deploy history reads exactly as an unchained one
/// (and so invariant 10's short-circuit rules keep applying to it unchanged).
/// </param>
/// <param name="TemplateId">For <see cref="BackupChainKind.TenantTeardown"/>: the tenant's template.</param>
/// <param name="Slug">For <see cref="BackupChainKind.TenantTeardown"/>: the tenant's slug.</param>
/// <param name="RemoveVolumes">For <see cref="BackupChainKind.TenantTeardown"/>: whether to destroy its data.</param>
public sealed record BackupChainStep(
    BackupChainKind Kind,
    int StackId,
    string? DeployTrigger = null,
    int TemplateId = 0,
    string? Slug = null,
    bool RemoveVolumes = false) {
    /// <summary>Deploy <paramref name="stackId"/> under <paramref name="trigger"/> once the backup succeeds.</summary>
    public static BackupChainStep ForDeploy(int stackId, string trigger) =>
        new(BackupChainKind.Deploy, stackId, trigger);

    /// <summary>Tear the tenant down once the backup succeeds; abort the removal if it does not.</summary>
    public static BackupChainStep ForTenantTeardown(
        int stackId, int templateId, string slug, bool removeVolumes) =>
        new(BackupChainKind.TenantTeardown, stackId, TemplateId: templateId, Slug: slug,
            RemoveVolumes: removeVolumes);
}

/// <summary>
/// Runs the work that must happen <em>after</em> a backup succeeds, and must not happen at all if it
/// fails: the pre-rollout deploy and the final backup before a tenant teardown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a coordinator and not a callback.</b> The backup queue and the deploy queue are separate by
/// design — one is single-flight process-wide (backups compete for the same disk, network and daemon),
/// the other is per stack behind an instance-wide gate. Chaining is therefore an explicit relationship
/// between two queues rather than something either queue can express: this class holds it, keyed by the
/// backup event id, and <see cref="BackupQueueService"/> tells it when a run reached a terminal state.
/// A backup event id is the right key because it is exactly what coalescing collapses onto — two
/// pre-deploy requests for one stack become one backup, and both their follow-ups then hang off the one
/// event and both run once it succeeds.
/// </para>
/// <para>
/// <b>What a failure does.</b> The follow-up does not happen, and the reason is written where the
/// operator will look for it. For a deploy that is a <em>failed <see cref="DeployEvent"/></em> on the
/// stack, carrying the trigger the deploy would have had and an output line naming the backup event —
/// so the stack's deploy history says "this did not deploy, and here is why" instead of silently
/// showing nothing. For a teardown it is an audit row: the tenant is still there, which is the visible
/// half, and the row says the removal was aborted.
/// </para>
/// <para>
/// In-memory, like both queues: a process that dies between the backup and its follow-up loses the
/// chain, and the backup event is the durable record that it happened. That is the same guarantee the
/// deploy queue itself gives (a queued deploy does not survive a restart either), and buying more would
/// mean a durable job table this feature does not justify.
/// </para>
/// </remarks>
/// <param name="deployQueue">The deploy queue a successful pre-deploy backup releases work onto.</param>
/// <param name="scopeFactory">Creates the scopes the teardown and the event writes run in.</param>
/// <param name="logger">Logger.</param>
public sealed class BackupChainCoordinator(
    DeployQueueService deployQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<BackupChainCoordinator> logger) {
    private readonly ConcurrentDictionary<int, List<BackupChainStep>> _steps = new();

    /// <summary>Attaches <paramref name="step"/> to the backup tracked by <paramref name="backupEventId"/>.</summary>
    /// <remarks>
    /// Several steps may hang off one event: coalescing merges two requests for the same stack onto one
    /// backup, and both callers still expect their follow-up. They run in the order they were attached.
    /// </remarks>
    public void Attach(int backupEventId, BackupChainStep step) =>
        _steps.AddOrUpdate(
            backupEventId,
            _ => [step],
            (_, existing) => { lock (existing) existing.Add(step); return existing; });

    /// <summary>
    /// Called by the backup worker once a run reached a terminal state. Runs (or refuses) every step
    /// attached to it, then forgets them.
    /// </summary>
    /// <param name="backupEventId">The backup event that just finished.</param>
    /// <param name="success">Whether it succeeded.</param>
    /// <param name="ct">Cancellation token; the refusal paths deliberately ignore it (see remarks).</param>
    public async Task OnBackupFinishedAsync(int backupEventId, bool success, CancellationToken ct) {
        if (!_steps.TryRemove(backupEventId, out var steps)) return;
        List<BackupChainStep> pending;
        lock (steps) pending = [.. steps];

        foreach (var step in pending) {
            try {
                if (success) await RunAsync(step, ct);
                else await BlockAsync(step, backupEventId);
            } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
                logger.LogError(
                    ex, "Chained {Kind} after backup event {EventId} failed for stack {StackId}",
                    step.Kind, backupEventId, step.StackId);
            }
        }
    }

    /// <summary>Whether anything is still waiting on <paramref name="backupEventId"/>. For tests and diagnostics.</summary>
    internal bool HasPending(int backupEventId) => _steps.ContainsKey(backupEventId);

    private async Task RunAsync(BackupChainStep step, CancellationToken ct) {
        switch (step.Kind) {
            case BackupChainKind.Deploy:
                deployQueue.Enqueue(step.StackId, step.DeployTrigger ?? DeployTriggers.Manual);
                break;
            case BackupChainKind.TenantTeardown:
                using (var scope = scopeFactory.CreateScope()) {
                    var teardown = scope.ServiceProvider.GetRequiredService<TenantTeardownService>();
                    var result = await teardown.TeardownAsync(
                        step.TemplateId, step.Slug, step.RemoveVolumes, ct);
                    if (result.Status == TenantTeardownStatus.Removed) {
                        await RecordTeardownAsync(scope, step, removed: true, result.Error);
                    } else if (result.Status == TenantTeardownStatus.TenantNotFound) {
                        // Already gone — the outcome this step wanted. Two steps can legitimately land
                        // on one backup (a double click coalesces onto the pending run and attaches a
                        // second teardown), so the second one finding nothing is success, not a failure
                        // to shout about; auditing it would put a red row under a removal that worked.
                        logger.LogInformation(
                            "Tenant {Slug} was already removed when its final-backup chain ran", step.Slug);
                    } else {
                        logger.LogWarning(
                            "Final backup of tenant {Slug} succeeded but the teardown did not: {Error}",
                            step.Slug, result.Error);
                        await RecordTeardownAsync(scope, step, removed: false, result.Error);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// The follow-up did not happen because the backup failed. Writes the trail wherever the operator
    /// would look for the thing that did not happen.
    /// </summary>
    private async Task BlockAsync(BackupChainStep step, int backupEventId) {
        using var scope = scopeFactory.CreateScope();
        switch (step.Kind) {
            case BackupChainKind.Deploy: {
                var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
                // The stack can be gone: a backup takes minutes and a delete (or a tenant teardown)
                // needs no permission from this queue. A DeployEvent for a stack that no longer exists
                // violates the foreign key, so the insert below would throw instead of recording
                // anything — and there is nobody left to read the record anyway.
                if (!await db.Stacks.AnyAsync(s => s.Id == step.StackId, CancellationToken.None)) {
                    logger.LogInformation(
                        "Pre-deploy backup {EventId} failed and stack {StackId} no longer exists; nothing to block",
                        backupEventId, step.StackId);
                    return;
                }
                var now = DateTimeOffset.UtcNow;
                // A real, failed deploy event: the operator asked for a deploy, no deploy happened, and
                // the deploy history is where that belongs. The trigger is the one the deploy would have
                // carried, so the row sits in the history reading as the deploy that was refused.
                db.DeployEvents.Add(new DeployEvent {
                    StackId = step.StackId,
                    TriggeredBy = step.DeployTrigger ?? DeployTriggers.Manual,
                    Status = "failed",
                    StartedAt = now,
                    FinishedAt = now,
                    Output = "[Watchtower] The pre-deploy backup failed, so this deploy did not run. "
                        + $"See backup run #{backupEventId} for the reason. Nothing was changed on this stack.",
                });
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(
                    "Pre-deploy backup {EventId} failed; the deploy of stack {StackId} was blocked",
                    backupEventId, step.StackId);
                break;
            }
            case BackupChainKind.TenantTeardown: {
                var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
                await audit.RecordAsync(
                    BackupService.AuditCategory, "tenant.remove.aborted", step.Slug ?? $"stack {step.StackId}",
                    $"the final backup (run #{backupEventId}) failed, so the tenant was not removed",
                    success: false, ct: CancellationToken.None);
                logger.LogWarning(
                    "Final backup {EventId} failed; the removal of tenant {Slug} was aborted",
                    backupEventId, step.Slug);
                break;
            }
        }
    }

    /// <summary>Audits the outcome of a chained teardown — removed, or attempted and refused.</summary>
    private static Task RecordTeardownAsync(
        IServiceScope scope, BackupChainStep step, bool removed, string? error) {
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
        return audit.RecordAsync(
            BackupService.AuditCategory, "tenant.remove.final-backup", step.Slug ?? $"stack {step.StackId}",
            removed
                ? "the final backup succeeded and the tenant was removed"
                : $"the final backup succeeded but the teardown did not: {error}",
            success: removed, error: removed ? null : error, ct: CancellationToken.None);
    }
}
