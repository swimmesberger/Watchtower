using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// What one release actually reached: a row per stack with the outcome of its deploy, and the counts
/// above them (docs/products/design.md §"Convergent fan-out", partial failure).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what <c>DeployEvent.ReleaseId</c> exists for.</b> The alternative — string-matching
/// <c>TriggeredBy</c> and guessing from timestamps — could not tell a fan-out of v43 from a fan-out of
/// v42 thirty seconds earlier, which is exactly the case a rollout view is opened for. The id is stamped
/// at execution, so a coalesced deploy reports the release that actually ran rather than the one that
/// was asked for (invariant 3).
/// </para>
/// <para>
/// <b>The newest event per stack wins.</b> A stack can have several events for one release — a retry, a
/// manual redeploy — and the row is about where the stack ended up, not about every attempt. Newest is
/// the highest id, for the same reason releases are ordered that way: two events of one burst can share
/// a timestamp.
/// </para>
/// <para>
/// <b>The skipped rows describe now, not then</b> — see the remarks on <see cref="ReleaseRolloutDto"/>.
/// </para>
/// </remarks>
[Handler("products.getReleaseRollout")]
public sealed class GetReleaseRollout(WatchtowerDbContext db)
    : IHandler<GetReleaseRollout.Query, Result<GetReleaseRollout.Response>> {
    public sealed record Query(int ReleaseId) : IQuery;

    public sealed record Response(ReleaseRolloutDto Rollout);

    /// <summary>A stopped stack was never targeted — the fan-out skips it in the query (ADR-0025).</summary>
    public const string SkippedStopped = "stopped";

    /// <summary>A pin is a standing "stay here", so no roll-out reaches the stack.</summary>
    public const string SkippedPinned = "pinned";

    /// <summary>Nothing refused it; it simply has no deploy event for this release.</summary>
    public const string SkippedNotDeployed = "not deployed";

    /// <summary>One deploy event as the query reads it, named so the per-stack fold can be typed.</summary>
    private sealed record DeployEventRow(
        int Id, int StackId, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var release = await db.Releases.AsNoTracking()
            .Where(r => r.Id == query.ReleaseId)
            .Select(r => new { r.Id, r.Version, r.ProductId })
            .FirstOrDefaultAsync(ct);
        if (release is null)
            return AppError.NotFound($"Release {query.ReleaseId} not found.");

        // Every event of this release, newest last, so the per-stack fold below keeps the newest.
        var events = await db.DeployEvents.AsNoTracking()
            .Where(e => e.ReleaseId == release.Id)
            .OrderBy(e => e.Id)
            .Select(e => new DeployEventRow(e.Id, e.StackId, e.Status, e.StartedAt, e.FinishedAt))
            .ToListAsync(ct);
        // Folded rather than ToDictionary: a stack legitimately has several events for one release, and
        // the later one simply overwrites the earlier.
        var newestByStack = new Dictionary<int, DeployEventRow>();
        foreach (var e in events) newestByStack[e.StackId] = e;

        // The product's stacks — both the ones with an event, to name them, and the ones without, which
        // is the whole skipped half. One query rather than a name lookup per row.
        var stacks = await db.Stacks.AsNoTracking()
            .Where(s => s.ProductId == release.ProductId)
            .OrderBy(s => s.Name)
            .Select(s => new {
                s.Id, s.Name, s.TenantSlug, s.DesiredState, s.PinnedReleaseId,
            })
            .ToListAsync(ct);

        var rows = new List<ReleaseRolloutStackDto>(stacks.Count);
        foreach (var stack in stacks) {
            if (newestByStack.TryGetValue(stack.Id, out var e)) {
                rows.Add(new ReleaseRolloutStackDto(
                    stack.Id, stack.Name, stack.TenantSlug, e.Status, e.StartedAt, e.FinishedAt, e.Id, null));
                continue;
            }
            rows.Add(new ReleaseRolloutStackDto(
                stack.Id, stack.Name, stack.TenantSlug, ReleaseRolloutDto.SkippedStatus, null, null, null,
                // Read off the stack as it is today, which is the honest limit of what can be known —
                // the fan-out records nothing per stack it skipped, deliberately.
                stack.DesiredState == StackDesiredState.Stopped ? SkippedStopped
                : stack.PinnedReleaseId is not null && stack.PinnedReleaseId != release.Id ? SkippedPinned
                : SkippedNotDeployed));
        }

        // A deploy event whose stack has since been deleted has already cascaded away, so every event
        // is matched by construction and the counts are over the rows.
        return new Response(new ReleaseRolloutDto(
            release.Id,
            release.Version,
            rows.Count(r => r.Status == DeployEventStatus.Succeeded),
            rows.Count(r => r.Status == DeployEventStatus.Failed),
            rows.Count(r => r.Status == DeployEventStatus.Queued),
            rows.Count(r => r.Status == DeployEventStatus.Running),
            rows.Count(r => r.Status == ReleaseRolloutDto.SkippedStatus),
            rows));
    }
}
