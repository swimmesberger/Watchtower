using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Fetches a single route by id.</summary>
[Handler("proxy.getRoute")]
public sealed class GetRoute(WatchtowerDbContext db)
    : IHandler<GetRoute.Query, Result<GetRoute.Response>> {
    public sealed record Query(int Id);
    public sealed record Response(RouteDto Route);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var route = await db.Routes.AsNoTracking()
            .Include(r => r.Stack)
            .Include(r => r.Realm)
            .FirstOrDefaultAsync(r => r.Id == query.Id, ct);
        if (route is null) return AppError.NotFound($"Route {query.Id} not found");

        var isLoginRoute = await db.Realms.AsNoTracking().AnyAsync(r => r.LoginRouteId == route.Id, ct);
        return new Response(RouteMapping.ToDto(route, isLoginRoute));
    }
}
