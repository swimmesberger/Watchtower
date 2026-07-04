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

        // SQLite can't ORDER BY a DateTimeOffset, so sort newest-first client-side.
        var events = await db.DeployEvents.AsNoTracking()
            .Where(e => e.StackId == query.StackId)
            .Select(e => new DeployEventDto(e.Id, e.StackId, e.TriggeredBy, e.Status, e.Output, e.StartedAt, e.FinishedAt))
            .ToListAsync(ct);
        return new Response([.. events.OrderByDescending(e => e.StartedAt)]);
    }
}
