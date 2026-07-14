using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Lists all configured routes with their target stack and provisioning status.</summary>
[Handler("proxy.listRoutes")]
public sealed class ListRoutes(WatchtowerDbContext db)
    : IHandler<ListRoutes.Query, Result<ListRoutes.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<RouteDto> Routes);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var routes = await db.Routes.AsNoTracking()
            .Include(r => r.Stack)
            .OrderBy(r => r.Domain)
            .ToListAsync(ct);
        return new Response(routes.Select(RouteMapping.ToDto).ToList());
    }
}
