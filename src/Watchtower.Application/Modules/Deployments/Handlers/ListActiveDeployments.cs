using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Deployments.Handlers;

/// <summary>
/// Returns all currently queued or running deploy events across every stack, enriched with the
/// stack name for dashboard display. Ordered oldest-first so they appear in processing order.
/// </summary>
[Handler("deployments.active")]
public sealed class ListActiveDeployments(WatchtowerDbContext db)
    : IHandler<ListActiveDeployments.Query, Result<ListActiveDeployments.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<ActiveDeploymentDto> Deployments);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // SQLite can't ORDER BY a DateTimeOffset, so sort client-side (the active set is tiny).
        var items = await db.DeployEvents.AsNoTracking()
            .Where(e => e.Status == "queued" || e.Status == "running")
            .Select(e => new ActiveDeploymentDto(
                e.Id, e.StackId, e.Stack!.Name, e.Status, e.TriggeredBy, e.StartedAt))
            .ToListAsync(ct);
        return new Response([.. items.OrderBy(d => d.StartedAt)]);
    }
}
