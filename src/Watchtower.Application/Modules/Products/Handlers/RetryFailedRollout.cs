using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Re-enqueues the stacks whose deploy of one release failed, and only those
/// (docs/products/design.md §"Convergent fan-out": "Retry failed — re-enqueue failed ids only").
/// </summary>
/// <remarks>
/// <para>
/// <b>It retries the instances, not the release.</b> The enqueue carries no release id, because no
/// enqueue in this system does (invariant 3): each re-enqueued deploy resolves
/// <c>PinnedReleaseId ?? newest</c> when it runs. So a stack pinned to the failed release does deploy
/// exactly it, and a latest-tracking stack deploys whatever is newest <em>now</em> — which may be a
/// release published since the failure. That is the convergent rule working as intended, and it is why
/// the button is worded "Retry failed instances (they deploy the newest release)" rather than "retry
/// this release": promising the specific release would be a promise this call deliberately does not
/// make. Capturing the id to keep that promise is the downgrade race design.md §"Convergent fan-out"
/// rejects.
/// </para>
/// <para>
/// <b>There is no automatic retry, on purpose.</b> A failing deploy usually fails identically, and
/// re-running it across two hundred tenants by itself is a self-inflicted denial of service. So this is
/// a button, and the button targets the failures rather than the fleet: re-deploying the stacks that
/// already succeeded would take a working fleet down and back up for nothing.
/// </para>
/// <para>
/// <b>Only the newest event per stack counts.</b> A stack whose deploy failed and was then redeployed
/// successfully is not a failure any more, and re-enqueueing it would undo the fix.
/// </para>
/// <para>
/// <b>Two exclusions, both of which would otherwise make the button lie.</b> A stopped stack refuses
/// deploys (ADR-0025), so enqueuing one only adds a second failure to the view. And a stack now pinned
/// to a <em>different</em> release is not part of this rollout at all any more — it would deploy its own
/// pin, which is neither a retry of this failure nor anything the reader asked for. Both are reported
/// as skipped rather than silently dropped.
/// </para>
/// <para>
/// <b>Git mode is refused</b>, following <c>stacks.setRelease</c> and <c>products.deployRelease</c>: in
/// that mode the resolver answers null before it looks at anything, so every deploy this enqueued would
/// clone the branch head — a fleet-wide branch-head deploy dressed up as a retry of a release.
/// </para>
/// <para>
/// <b>Audited as a rollout</b>, under <c>products.deployRelease</c>'s own action rather than a new one:
/// "when was this release rolled out, by whom, and how far did it get" should be one filter on the
/// trail whichever button did it. The detail says it was a retry.
/// </para>
/// </remarks>
[Handler("products.retryFailedRollout")]
public sealed class RetryFailedRollout(
    WatchtowerDbContext db, DeployQueueService deployQueue, AuditLog audit, ICurrentUser currentUser)
    : IHandler<RetryFailedRollout.Command, Result<RetryFailedRollout.Response>> {
    public sealed record Command(int ReleaseId);

    /// <param name="Retried">How many stacks were re-enqueued.</param>
    /// <param name="Skipped">
    /// Failed stacks that were deliberately not re-enqueued because they are stopped or now pinned
    /// elsewhere — see the remarks on this handler.
    /// </param>
    /// <param name="DeployEventIds">The tracking events, one per re-enqueued stack.</param>
    public sealed record Response(int Retried, int Skipped, IReadOnlyList<int> DeployEventIds);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var release = await db.Releases.AsNoTracking()
            .Where(r => r.Id == command.ReleaseId)
            .Select(r => new {
                r.Id, r.Version, r.ProductId, ProductName = r.Product.Name, r.Product.ReleaseMode,
            })
            .FirstOrDefaultAsync(ct);
        if (release is null)
            return AppError.NotFound($"Release {command.ReleaseId} not found.");
        // The setRelease / deployRelease precedent, and the same sentence: in Git mode every deploy this
        // enqueued would clone the branch head, so a "retry" would quietly become a fleet-wide
        // branch-head deploy.
        if (release.ReleaseMode != ProductReleaseMode.Releases) {
            return AppError.Conflict(
                $"Product '{release.ProductName}' is in Git mode, so its stacks deploy the branch head "
                + "rather than a release. Switch it to release mode first.");
        }

        // Newest event per stack, folded in id order — the same rule products.getReleaseRollout applies,
        // so the button retries exactly the rows the view called failed.
        var events = await db.DeployEvents.AsNoTracking()
            .Where(e => e.ReleaseId == release.Id)
            .OrderBy(e => e.Id)
            .Select(e => new { e.StackId, e.Status })
            .ToListAsync(ct);
        var newestByStack = new Dictionary<int, string>();
        foreach (var e in events) newestByStack[e.StackId] = e.Status;
        var failedStackIds = newestByStack
            .Where(kv => kv.Value == DeployEventStatus.Failed)
            .Select(kv => kv.Key)
            .ToList();
        if (failedStackIds.Count == 0) return new Response(0, 0, []);

        var candidates = await db.Stacks.AsNoTracking()
            .Where(s => failedStackIds.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.DesiredState, s.PinnedReleaseId })
            .ToListAsync(ct);

        var targets = candidates
            .Where(s => s.DesiredState != StackDesiredState.Stopped
                && (s.PinnedReleaseId is null || s.PinnedReleaseId == release.Id))
            .Select(s => s.Id)
            .ToList();

        var deployEventIds = new List<int>(targets.Count);
        foreach (var stackId in targets)
            deployEventIds.Add(deployQueue.Enqueue(stackId, DeployTriggers.ReleaseManual).DeployEventId);

        var skipped = candidates.Count - targets.Count;
        await audit.RecordAsync(
            ProductMapping.AuditCategory, DeployRelease.AuditAction,
            $"{release.ProductName}/{release.Version}",
            $"retry of failed deploys: {targets.Count} stack(s) re-enqueued"
            + (skipped > 0 ? $", {skipped} skipped (stopped or pinned elsewhere)" : string.Empty),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(targets.Count, skipped, deployEventIds);
    }
}
