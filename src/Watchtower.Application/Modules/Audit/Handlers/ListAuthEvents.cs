using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Audit.Handlers;

/// <summary>
/// Reads one page of the audit trail, newest first, optionally narrowed to a kind, an account or an app.
/// </summary>
/// <remarks>
/// Keyset paging on the primary key, not offset paging: the trail is append-only and being written while
/// it is read, so an offset page would shift under the reader and skip or repeat rows on every "load
/// more". A cursor of "the last id I saw" is stable regardless of what arrives in the meantime.
/// </remarks>
[Handler("audit.list")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListAuthEvents(WatchtowerDbContext db)
    : IHandler<ListAuthEvents.Query, Result<ListAuthEvents.Response>> {
    /// <summary>
    /// One page request. Every field is optional and the filters combine (they are ANDed).
    /// </summary>
    /// <param name="BeforeId">
    /// The cursor: return only rows older than this id. Omitted means "start at the newest row" — pass
    /// back the previous response's <see cref="Response.NextBeforeId"/> to continue.
    /// </param>
    /// <param name="Limit">
    /// Page size, clamped to <see cref="AuditMapping.MaxLimit"/> and defaulting to
    /// <see cref="AuditMapping.DefaultLimit"/>.
    /// </param>
    /// <param name="Kind">Exact <see cref="Entities.AuthEvent.Kind"/> match — the values <c>audit.kinds</c> lists.</param>
    /// <param name="UserId">Only rows naming this account.</param>
    /// <param name="RouteId">Only rows naming this app.</param>
    public sealed record Query(
        int? BeforeId = null,
        int? Limit = null,
        string? Kind = null,
        int? UserId = null,
        int? RouteId = null);

    /// <param name="NextBeforeId">
    /// The cursor for the next page, or <see langword="null"/> when this page was the last one. Set
    /// whenever the page came back full: one more read may find nothing, but the alternative — counting
    /// the remainder — is a second query over an unbounded table to answer a question a disabled button
    /// already answers well enough.
    /// </param>
    public sealed record Response(IReadOnlyList<AuthEventDto> Events, int? NextBeforeId);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(query);

        var rows = db.AuthEvents.AsNoTracking();

        if (query.BeforeId is { } before) rows = rows.Where(e => e.Id < before);
        if (!string.IsNullOrEmpty(query.Kind)) rows = rows.Where(e => e.Kind == query.Kind);
        if (query.UserId is { } userId) rows = rows.Where(e => e.UserId == userId);
        if (query.RouteId is { } routeId) rows = rows.Where(e => e.RouteId == routeId);

        var limit = AuditMapping.ClampLimit(query.Limit);

        // Ordered by id, not by CreatedAt: EF Core's SQLite provider cannot translate an ORDER BY over a
        // DateTimeOffset at all (the same limitation ListDeployEvents works around). The key is an
        // autoincrementing surrogate over an append-only table, so descending id *is* newest-first — and
        // unlike the timestamp it is unique, which is what makes it usable as a paging cursor.
        var events = await rows
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .Select(AuditMapping.Projection)
            .ToListAsync(ct);

        var nextBeforeId = events.Count == limit ? events[^1].Id : (int?)null;
        return new Response(events, nextBeforeId);
    }
}
