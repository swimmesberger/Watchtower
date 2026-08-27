using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Returns backup history, newest first — for one stack (<see cref="Query.StackId"/>), for every
/// deployment of one product (<see cref="Query.ProductId"/>, the product Backups tab's fleet history),
/// for Watchtower's own database (<see cref="Query.Kind"/>), or instance-wide. Output is included, so
/// the UI can show the run log inline.
/// </summary>
/// <remarks>
/// The filters are independent and all optional, which is what keeps this additive: every existing
/// caller passes a stack id or nothing and gets exactly what it always did. Passing both narrows to the
/// intersection rather than being refused — a stack of the product answers both questions, and a stack
/// of another product legitimately answers neither.
/// </remarks>
[Handler("backups.events")]
public sealed class ListBackupEvents(WatchtowerDbContext db)
    : IHandler<ListBackupEvents.Query, Result<ListBackupEvents.Response>> {
    /// <param name="StackId">One stack, or null for every stack the other filters allow.</param>
    /// <param name="Limit">Row cap, clamped to 1…500.</param>
    /// <param name="ProductId">Every deployment of this product, or null for no product filter.</param>
    /// <param name="Kind">
    /// <c>stack</c> for the stack runs, <c>instance</c> for Watchtower's own (ADR-0027), or null for
    /// both. Unfiltered stays the default so the existing history views are unchanged: an instance run
    /// is part of "what has this Watchtower been backing up" and belongs in the instance-wide list.
    /// </param>
    public sealed record Query(int? StackId = null, int Limit = 50, int? ProductId = null, string? Kind = null);

    public sealed record Response(IReadOnlyList<BackupEventDto> Events);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (query.StackId is { } stackId && !await db.Stacks.AnyAsync(s => s.Id == stackId, ct))
            return AppError.NotFound($"Stack {stackId} not found");
        if (query.ProductId is { } productId && !await db.Products.AnyAsync(p => p.Id == productId, ct))
            return AppError.NotFound($"Product {productId} not found");

        var kind = query.Kind?.Trim().ToLowerInvariant();
        if (kind is not (null or "" or BackupEventKinds.Stack or BackupEventKinds.Instance))
            return AppError.Validation(
                $"Kind must be '{BackupEventKinds.Stack}', '{BackupEventKinds.Instance}', or omitted.");
        var instanceOnly = kind == BackupEventKinds.Instance;
        var stackOnly = kind == BackupEventKinds.Stack;

        var limit = Math.Clamp(query.Limit, 1, 500);
        // Id breaks ties: a stack-wide run writes several events within the same clock tick, and the
        // limit below only means something over a total order.
        var events = await db.BackupEvents.AsNoTracking()
            .Where(e => query.StackId == null || e.StackId == query.StackId)
            .Where(e => query.ProductId == null || e.Stack!.ProductId == query.ProductId)
            .Where(e => (!instanceOnly || e.StackId == null) && (!stackOnly || e.StackId != null))
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new BackupEventDto(
                e.Id, e.StackId, e.Stack == null ? null : e.Stack.Name, e.TriggeredBy, e.Status,
                e.RemotePath, e.SizeBytes, e.Output, e.StartedAt, e.FinishedAt,
                e.StackId == null ? BackupEventKinds.Instance : BackupEventKinds.Stack))
            .ToListAsync(ct);
        return new Response(events);
    }
}
