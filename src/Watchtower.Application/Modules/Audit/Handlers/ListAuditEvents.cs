using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Audit.Handlers;

/// <summary>One audit entry for the UI, newest-first.</summary>
public sealed record AuditEventDto(
    int Id,
    DateTimeOffset At,
    string Category,
    string Action,
    string Target,
    string? Detail,
    string? Actor,
    bool Success,
    string? Error);

/// <summary>The paging arithmetic the Audit readers share.</summary>
public static class AuditPaging {
    /// <summary>Rows returned when the caller names no limit — one screenful of "load more".</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// The most rows one call may return. A ceiling rather than a refusal: an over-large limit is a
    /// client that has not been taught to page, not a caller doing something wrong — clamping keeps it
    /// working and keeps the response bounded.
    /// </summary>
    public const int MaxLimit = 500;

    /// <summary>Normalizes a requested page size into the accepted range.</summary>
    public static int ClampLimit(int? limit) =>
        limit is not { } value || value <= 0 ? DefaultLimit : Math.Min(value, MaxLimit);
}

/// <summary>
/// Reads one page of the audit trail, newest first, optionally narrowed by category (a prefix —
/// <c>proxy</c> matches every proxy provider's events, <c>proxy.cloudflare</c> just Cloudflare's),
/// exact action, or exact actor. Admin-gated: the trail names accounts, hostnames, tunnels and raw
/// error messages across every realm.
/// </summary>
/// <remarks>
/// Keyset paging on the primary key, not offset paging: the trail is append-only and being written
/// while it is read, so an offset page would shift under the reader and skip or repeat rows on every
/// "load more". A cursor of "the last id I saw" is stable regardless of what arrives in the meantime.
/// Ordered by id rather than CreatedAt: EF Core's SQLite provider cannot translate an ORDER BY over a
/// DateTimeOffset, and over an append-only table the autoincrementing key IS arrival order — and,
/// unlike the timestamp, unique, which is what makes it usable as a cursor.
/// </remarks>
[Handler("audit.listEvents")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListAuditEvents(WatchtowerDbContext db)
    : IHandler<ListAuditEvents.Query, Result<ListAuditEvents.Response>> {
    /// <param name="Category">Category prefix filter; null lists every category.</param>
    /// <param name="Action">Exact action match — the values <c>audit.listFacets</c> lists.</param>
    /// <param name="Actor">Exact actor match; <c>system</c> selects rows with no actor.</param>
    /// <param name="BeforeId">
    /// The cursor: only rows older than this id. Omitted means "start at the newest row" — pass back the
    /// previous response's <see cref="Response.NextBeforeId"/> to continue.
    /// </param>
    /// <param name="Limit">Page size, clamped to 1–<see cref="AuditPaging.MaxLimit"/>, default <see cref="AuditPaging.DefaultLimit"/>.</param>
    public sealed record Query(
        string? Category = null,
        string? Action = null,
        string? Actor = null,
        int? BeforeId = null,
        int? Limit = null);

    /// <param name="NextBeforeId">
    /// The cursor for the next page, or null when this page was the last one. Set whenever the page came
    /// back full: one more read may find nothing, but counting the remainder would be a second query over
    /// the whole table to answer a question a disabled button already answers well enough.
    /// </param>
    public sealed record Response(IReadOnlyList<AuditEventDto> Events, int? NextBeforeId);

    /// <summary>The actor filter value that selects rows written with no actor — what the UI shows for them.</summary>
    public const string SystemActor = "system";

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var source = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Category)) {
            var prefix = query.Category.Trim();
            source = source.Where(e => e.Category == prefix || e.Category.StartsWith(prefix + "."));
        }
        if (!string.IsNullOrWhiteSpace(query.Action)) {
            var action = query.Action.Trim();
            source = source.Where(e => e.Action == action);
        }
        if (!string.IsNullOrWhiteSpace(query.Actor)) {
            var actor = query.Actor.Trim();
            source = actor == SystemActor
                ? source.Where(e => e.Actor == null)
                : source.Where(e => e.Actor == actor);
        }
        if (query.BeforeId is { } before) source = source.Where(e => e.Id < before);

        var limit = AuditPaging.ClampLimit(query.Limit);
        var events = await source
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new AuditEventDto(
                e.Id, e.CreatedAt, e.Category, e.Action, e.Target, e.Detail, e.Actor, e.Success, e.Error))
            .ToListAsync(ct);

        var nextBeforeId = events.Count == limit ? events[^1].Id : (int?)null;
        return new Response(events, nextBeforeId);
    }
}
