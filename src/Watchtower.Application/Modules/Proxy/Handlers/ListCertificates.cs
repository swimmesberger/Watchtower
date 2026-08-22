using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// What the in-process proxy holds, or is trying to hold, per host — ADR-0017 (forthcoming).
/// </summary>
/// <param name="Source">
/// Where the host comes from: <c>route</c> for a domain in the route table, <c>loginHost</c> for a
/// realm's login page (which Watchtower serves itself and which therefore needs a certificate too, with
/// no route row behind it), <c>orphan</c> for a certificate still on disk that nothing routes to.
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
        // moments ago may not have been projected yet — and a host with no id at all is exactly what
        // distinguishes a login host from a routed one.
        var routeIds = await db.Routes.AsNoTracking()
            .Select(r => new { r.Id, r.Domain })
            .ToDictionaryAsync(r => r.Domain, r => r.Id, StringComparer.OrdinalIgnoreCase, ct);

        var listed = certificates.Snapshot().Select(s => new CertificateDto(
            Host: s.Host,
            Source: routeIds.ContainsKey(s.Host) ? "route" : s.Desired ? "loginHost" : "orphan",
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
