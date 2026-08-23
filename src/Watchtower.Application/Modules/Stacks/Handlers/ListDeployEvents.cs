using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Returns deployment history for a stack, newest first.</summary>
[Handler("stacks.events")]
public sealed class ListDeployEvents(WatchtowerDbContext db)
    : IHandler<ListDeployEvents.Query, Result<ListDeployEvents.Response>> {
    public sealed record Query(int StackId);
    public sealed record Response(IReadOnlyList<DeployEventDto> Events);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (!await db.Stacks.AnyAsync(s => s.Id == query.StackId, ct))
            return AppError.NotFound($"Stack {query.StackId} not found");

        // Id breaks ties: two events of the same deploy burst can share a timestamp, and "newest first"
        // has to be a total order or the page reshuffles between reads.
        var events = await db.DeployEvents.AsNoTracking()
            .Where(e => e.StackId == query.StackId)
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Select(e => new DeployEventDto(e.Id, e.StackId, e.TriggeredBy, e.Status, e.Output, e.StartedAt, e.FinishedAt))
            .ToListAsync(ct);
        return new Response(events);
    }
}
