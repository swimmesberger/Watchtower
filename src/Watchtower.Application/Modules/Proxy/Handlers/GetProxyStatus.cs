using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Reports whether the reverse proxy is enabled and running, which provider serves it, and the route count.</summary>
[Handler("proxy.getStatus")]
public sealed class GetProxyStatus(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    IOptionsMonitor<WatchtowerOptions> options,
    YarpListenerState listener)
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
        var detail = proxyOptions.ResolveProvider() == ProxyProviderKind.Yarp && proxy.Enabled && !listener.HttpsBound
            ? "HTTPS listener not bound — routes are served over plain HTTP only"
            : null;
        return new Response(proxy.Enabled, running, count, proxyOptions.ProviderName(), detail);
    }
}
