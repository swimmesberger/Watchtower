using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Reports whether the reverse proxy is enabled and running, which provider serves it, and the route count.</summary>
[Handler("proxy.getStatus")]
public sealed class GetProxyStatus(
    WatchtowerDbContext db, IProxyProvider proxy, IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<GetProxyStatus.Query, Result<GetProxyStatus.Response>> {
    public sealed record Query;

    /// <param name="CaddyRunning">
    /// Whether the active provider's data plane reports running. Historical name kept for the wire
    /// contract; with the cloudflare provider it reflects the cloudflared container (or "configured"
    /// in fully-unmanaged mode).
    /// </param>
    public sealed record Response(bool Enabled, bool CaddyRunning, int RouteCount, string Provider);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var count = await db.Routes.CountAsync(ct);
        var running = await proxy.IsRunningAsync(ct);
        var provider = options.CurrentValue.Proxy.ResolveProvider() == ProxyProviderKind.Cloudflare ? "cloudflare" : "caddy";
        return new Response(proxy.Enabled, running, count, provider);
    }
}
