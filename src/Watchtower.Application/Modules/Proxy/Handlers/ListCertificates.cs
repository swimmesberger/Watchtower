using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// What the in-process proxy holds, or is trying to hold, per host — ADR-0022.
/// </summary>
/// <param name="Source">
/// Where the host comes from: <c>route</c> for a domain in the route table — Watchtower's own hostnames
/// included, since ADR-0023 made those rows like any other — or <c>orphan</c> for a certificate still on
/// disk that nothing routes to.
/// </param>
/// <param name="State">One of <c>none</c>, <c>pending</c>, <c>active</c>, <c>awaitingDns</c>, <c>error</c>.</param>
public sealed record CertificateDto(
    string Host,
    string Source,
    int? RouteId,
    string State,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    string? Issuer,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    int ConsecutiveFailures);

/// <summary>
/// Lists the certificate state of every host the in-process proxy cares about. Reads process state and
/// the route table only — no CA is contacted, so the page is cheap to refresh.
/// </summary>
[Handler("proxy.listCertificates")]
public sealed class ListCertificates(CertificateManager certificates, WatchtowerDbContext db)
    : IHandler<ListCertificates.Query, Result<ListCertificates.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<CertificateDto> Certificates);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // Route ids come from the database rather than from the routing table, because a route created
        // moments ago may not have been projected yet — and a host with no row at all is a certificate
        // nothing routes to any more.
        // Keyed by hostname, so the rows without one — the port routes (ADR-0033) — are left out. Their
        // certificate is the internal CA's shared LAN leaf, which is not a route's certificate at all:
        // one leaf serves every port route at once.
        var rows = await db.Routes.AsNoTracking().Select(r => new { r.Id, r.Domain }).ToListAsync(ct);
        var routeIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) {
            if (row.Domain is { } domain) routeIds[domain] = row.Id;
        }

        var listed = certificates.Snapshot().Select(s => new CertificateDto(
            Host: s.Host,
            Source: routeIds.ContainsKey(s.Host) ? "route" : "orphan",
            RouteId: routeIds.TryGetValue(s.Host, out var id) ? id : null,
            State: s.State,
            NotBefore: s.NotBefore,
            NotAfter: s.NotAfter,
            Issuer: s.Issuer,
            LastAttemptAt: s.LastAttemptAt,
            LastError: s.LastError,
            NextAttemptAt: s.NextAttemptAt,
            ConsecutiveFailures: s.ConsecutiveFailures)).ToArray();

        return new Response(listed);
    }
}
