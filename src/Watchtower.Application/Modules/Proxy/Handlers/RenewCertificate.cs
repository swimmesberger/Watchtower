using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Orders a certificate for one host immediately, bypassing the renewal window and any backoff — the
/// operator's escape hatch when a domain's DNS has just been fixed and waiting out a six-hour rung is
/// not acceptable.
/// </summary>
/// <remarks>
/// Restricted to hosts the proxy already wants a certificate for. The alternative — issuing for any name
/// an operator types — would turn a UI button into an unauthenticated-by-DNS way to spend the
/// deployment's ACME rate limit on names it does not serve.
/// <para>
/// The attempt is awaited rather than queued, because the whole point is to see the outcome: the response
/// carries the host's state after the attempt, error and all.
/// </para>
/// </remarks>
[Handler("proxy.renewCertificate")]
public sealed class RenewCertificate(
    CertificateManager certificates, WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<RenewCertificate.Command, Result<RenewCertificate.Response>> {
    public sealed record Command(string Host);
    public sealed record Response(CertificateDto Certificate);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (!DesiredHosts.TryNormalize(command.Host, out var host, out var reason))
            return AppError.Validation(reason);

        var known = certificates.Snapshot().FirstOrDefault(s => s.Host == host);
        if (known is null || !known.Desired)
            return AppError.Validation($"'{host}' is not a host the in-process proxy serves.");

        await audit.RecordAsync(
            CertificateIssuer.AuditCategory, "cert.renew.request", host, detail: null,
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // The outcome itself is not inspected: whatever happened is already folded into the manager's
        // per-host state, and reporting from one place keeps this response identical to what the list
        // will show a second later.
        await certificates.RenewNowAsync(host, ct);

        var routeId = await db.Routes.AsNoTracking()
            .Where(r => r.Domain == host)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct);
        var state = certificates.Snapshot().FirstOrDefault(s => s.Host == host) ?? known;

        return new Response(new CertificateDto(
            Host: state.Host,
            Source: routeId is null ? "loginHost" : "route",
            RouteId: routeId,
            State: state.State,
            NotBefore: state.NotBefore,
            NotAfter: state.NotAfter,
            Issuer: state.Issuer,
            LastAttemptAt: state.LastAttemptAt,
            LastError: state.LastError,
            NextAttemptAt: state.NextAttemptAt,
            ConsecutiveFailures: state.ConsecutiveFailures));
    }
}
