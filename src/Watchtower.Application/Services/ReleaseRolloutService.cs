using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>What one fan-out targeted, and the deploy events tracking it.</summary>
/// <param name="StacksEnqueued">
/// How many stacks the predicate selected. The number the release webhook answers with, and the number
/// the roll-out dialog shows.
/// </param>
/// <param name="DeployEventIds">
/// The tracking event per targeted stack, in stack-id order. Coalescing means two of these can be the
/// same id — a stack with a deploy already pending is asked once and answers with the pending event.
/// </param>
public sealed record ReleaseRolloutResult(int StacksEnqueued, IReadOnlyList<int> DeployEventIds) {
    /// <summary>Nothing was targeted — the product has no eligible stacks.</summary>
    public static readonly ReleaseRolloutResult None = new(0, []);
}

/// <summary>
/// Fans a release out to the stacks that should take it: on release creation
/// (<see cref="DeployTriggers.Release"/>) and for the operator's explicit "deploy latest everywhere"
/// (<see cref="DeployTriggers.ReleaseManual"/>) — docs/products/design.md §Convergent fan-out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The enqueue carries no release id.</b> That is the whole design (invariant 3): each deploy
/// resolves <c>PinnedReleaseId ?? newest</c> when it runs, so two releases 30 s apart with a deploy
/// mid-flight collapse into one pending deploy that runs the <em>newer</em> release, never the
/// superseded one. Handing the id down instead would need "skip if newer already deployed" logic — i.e.
/// execution-time resolution rebuilt with more moving parts and a downgrade window in between.
/// </para>
/// <para>
/// <b>Nothing here waits.</b> <see cref="DeployQueueService.Enqueue"/> starts a worker per stack and the
/// instance-wide gate bounds how many actually run, so a 200-tenant fan-out returns as fast as it can
/// write 200 rows and drains behind the gate.
/// </para>
/// </remarks>
public class ReleaseRolloutService(WatchtowerDbContext db, DeployQueueService deployQueue) {
    /// <summary>
    /// Enqueues a deploy for every stack of <paramref name="productId"/> that tracks latest and is
    /// running, with <see cref="AutoDeployMode.OnChange"/> — the automatic rollout a new release
    /// triggers.
    /// </summary>
    /// <remarks>
    /// The predicate is the design's, clause for clause:
    /// <list type="bullet">
    /// <item><c>pinned_release_id IS NULL</c> — a pin is the opt-out from all automation (rule 2);</item>
    /// <item><c>desired_state = 'Running'</c> — a stopped stack is deliberately disabled (ADR-0025), and
    /// skipping it in the query rather than letting the deploy refuse keeps the release view free of
    /// failed-deploy noise;</item>
    /// <item><c>auto_deploy_mode = 'OnChange'</c> — the mode's intent with the mechanism swapped from
    /// pull to push (rule 3). <c>Off</c> is badge-only and <c>Scheduled</c> waits for its window.</item>
    /// </list>
    /// It does <em>not</em> check the product's mode: the only caller that reaches here for a
    /// <c>Git</c>-mode product would be one that just created a release, which flips the mode in the
    /// same transaction — so by the time this runs the product is in <c>Releases</c> mode by
    /// construction, and re-reading it would only add a query that can never change the answer.
    /// </remarks>
    /// <param name="productId">The product whose stacks to roll forward.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ReleaseRolloutResult> EnqueueForProductAsync(int productId, CancellationToken ct) =>
        EnqueueAsync(
            db.Stacks.Where(s =>
                s.ProductId == productId
                && s.PinnedReleaseId == null
                && s.DesiredState == StackDesiredState.Running
                && s.AutoDeployMode == AutoDeployMode.OnChange),
            DeployTriggers.Release,
            ct);

    /// <summary>
    /// Enqueues a deploy for every latest-tracking, running stack of <paramref name="productId"/> —
    /// the operator's explicit "deploy the latest release everywhere" (<c>products.deployRelease</c>).
    /// </summary>
    /// <remarks>
    /// Same predicate as <see cref="EnqueueForProductAsync"/> minus the <see cref="AutoDeployMode"/>
    /// clause, and deliberately so. <c>AutoDeployMode</c> answers "should this stack deploy by itself?";
    /// an operator pressing a button is not the stack deploying by itself, and a fleet kept on
    /// <c>Off</c> for exactly that reason — the canary workflow in design.md §Rollback and canary is
    /// built on it — would otherwise find the button did nothing at all. Pinned and stopped stacks are
    /// still excluded: a pin is a standing instruction about which release to run, and a stopped stack
    /// would only produce a failed deploy.
    /// </remarks>
    /// <param name="productId">The product whose stacks to roll forward.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ReleaseRolloutResult> EnqueueLatestForProductAsync(int productId, CancellationToken ct) =>
        EnqueueAsync(
            db.Stacks.Where(s =>
                s.ProductId == productId
                && s.PinnedReleaseId == null
                && s.DesiredState == StackDesiredState.Running),
            DeployTriggers.ReleaseManual,
            ct);

    /// <summary>Reads the ids the predicate selects and enqueues one deploy per stack, in id order.</summary>
    /// <remarks>
    /// Ids first, then enqueues: <see cref="DeployQueueService.Enqueue"/> writes its own tracking row
    /// through its own scope, so holding an open reader over the same context while it does would be a
    /// second command on one connection.
    /// </remarks>
    private async Task<ReleaseRolloutResult> EnqueueAsync(
        IQueryable<Stack> targets, string trigger, CancellationToken ct) {
        var stackIds = await targets
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (stackIds.Count == 0) return ReleaseRolloutResult.None;

        var eventIds = new List<int>(stackIds.Count);
        foreach (var stackId in stackIds)
            eventIds.Add(deployQueue.Enqueue(stackId, trigger).DeployEventId);
        return new ReleaseRolloutResult(stackIds.Count, eventIds);
    }
}
