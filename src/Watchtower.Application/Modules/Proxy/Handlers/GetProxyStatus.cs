using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Reports whether the reverse proxy is enabled and running, plus the number of routes.</summary>
[Handler("proxy.getStatus")]
public sealed class GetProxyStatus(WatchtowerDbContext db, CaddyManager caddy)
    : IHandler<GetProxyStatus.Query, Result<GetProxyStatus.Response>> {
    public sealed record Query;
    public sealed record Response(bool Enabled, bool CaddyRunning, int RouteCount);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var count = await db.Routes.CountAsync(ct);
        var running = await caddy.IsCaddyRunningAsync(ct);
        return new Response(caddy.Enabled, running, count);
    }
}
