using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Returns backup history, newest first — for one stack (<see cref="Query.StackId"/>) or
/// instance-wide. Output is included, so the UI can show the run log inline.
/// </summary>
[Handler("backups.events")]
public sealed class ListBackupEvents(WatchtowerDbContext db)
    : IHandler<ListBackupEvents.Query, Result<ListBackupEvents.Response>> {
    public sealed record Query(int? StackId = null, int Limit = 50);

    public sealed record Response(IReadOnlyList<BackupEventDto> Events);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (query.StackId is { } stackId && !await db.Stacks.AnyAsync(s => s.Id == stackId, ct))
            return AppError.NotFound($"Stack {stackId} not found");

        var limit = Math.Clamp(query.Limit, 1, 500);
        // SQLite can't ORDER BY a DateTimeOffset, so sort newest-first client-side (on the
        // autoincrement id, which orders identically for events created by one process).
        var events = await db.BackupEvents.AsNoTracking()
            .Where(e => query.StackId == null || e.StackId == query.StackId)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new BackupEventDto(
                e.Id, e.StackId, e.Stack!.Name, e.TriggeredBy, e.Status, e.RemotePath, e.SizeBytes,
                e.Output, e.StartedAt, e.FinishedAt))
            .ToListAsync(ct);
        return new Response(events);
    }
}
