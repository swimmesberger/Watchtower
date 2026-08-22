using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Reports whether the reverse proxy is enabled and running, which provider serves it, and the route count.</summary>
[Handler("proxy.getStatus")]
public sealed class GetProxyStatus(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    IOptionsMonitor<WatchtowerOptions> options,
    YarpListenerState listener,
    CertificateManager certificates)
    : IHandler<GetProxyStatus.Query, Result<GetProxyStatus.Response>> {
    public sealed record Query;

    /// <param name="CaddyRunning">
    /// Whether the active provider's data plane reports running. Historical name kept for the wire
    /// contract; with the cloudflare provider it reflects the cloudflared container (or "configured"
    /// in fully-unmanaged mode), and with the in-process provider whether the HTTPS listener is bound.
    /// </param>
    /// <param name="ProviderDetail">
    /// A provider-specific caveat worth surfacing next to the status, or null when there is nothing to
    /// say. It exists for the one state that is otherwise invisible: the in-process proxy running with
    /// no HTTPS listener, where routes resolve and are served — just over plain HTTP.
    /// </param>
    public sealed record Response(
        bool Enabled, bool CaddyRunning, int RouteCount, string Provider, string? ProviderDetail);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var count = await db.Routes.CountAsync(ct);
        var running = await proxy.IsRunningAsync(ct);
        var proxyOptions = options.CurrentValue.Proxy;
        var yarp = proxyOptions.ResolveProvider() == ProxyProviderKind.Yarp && proxy.Enabled;
        var detail = yarp switch {
            true when !listener.HttpsBound => "HTTPS listener not bound — routes are served over plain HTTP only",
            // Only while there is something outstanding: "12 of 12 issued" is noise next to a status that
            // already says the proxy is running.
            true => CertificateProgress(),
            _ => null,
        };
        return new Response(proxy.Enabled, running, count, proxyOptions.ProviderName(), detail);
    }

    /// <summary>
    /// How far through issuance the proxy is, or null when there is nothing outstanding. Counted off the
    /// manager's own view rather than the route rows, so a login host waiting for its certificate is
    /// included — it has no route row and would otherwise be invisible here.
    /// </summary>
    private string? CertificateProgress() {
        var desired = certificates.Snapshot().Where(s => s.Desired).ToArray();
        if (desired.Length == 0) return null;
        var active = desired.Count(s => s.State == "active");
        return active == desired.Length ? null : $"{active} of {desired.Length} certificates issued";
    }
}
