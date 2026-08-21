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

/// <summary>
/// Lists the audit trail, newest-first, optionally narrowed to a category prefix — <c>proxy</c>
/// matches every proxy provider's events, <c>proxy.cloudflare</c> just Cloudflare's. Admin-gated:
/// the trail names hostnames, tunnels and error details an ordinary operator page doesn't need.
/// </summary>
[Handler("audit.listEvents")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListAuditEvents(WatchtowerDbContext db)
    : IHandler<ListAuditEvents.Query, Result<ListAuditEvents.Response>> {
    /// <param name="Category">Category prefix filter; null lists every category.</param>
    /// <param name="Limit">Newest rows returned; clamped to 1–500, default 100.</param>
    public sealed record Query(string? Category = null, int? Limit = null);

    public sealed record Response(IReadOnlyList<AuditEventDto> Events);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var limit = Math.Clamp(query.Limit ?? 100, 1, 500);
        var source = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Category)) {
            var prefix = query.Category.Trim();
            source = source.Where(e => e.Category == prefix || e.Category.StartsWith(prefix + "."));
        }
        var events = await source
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new AuditEventDto(
                e.Id, e.CreatedAt, e.Category, e.Action, e.Target, e.Detail, e.Actor, e.Success, e.Error))
            .ToListAsync(ct);
        return new Response(events);
    }
}
