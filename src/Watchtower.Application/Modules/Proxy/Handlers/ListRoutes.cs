using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Lists all configured routes with their target stack or realm and their provisioning status.</summary>
[Handler("proxy.listRoutes")]
public sealed class ListRoutes(WatchtowerDbContext db)
    : IHandler<ListRoutes.Query, Result<ListRoutes.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<RouteDto> Routes);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var routes = await db.Routes.AsNoTracking()
            .Include(r => r.Stack)
            .Include(r => r.Realm)
            .OrderBy(r => r.Domain)
            .ToListAsync(ct);

        // One query for the whole table rather than a navigation per row: "is this route a realm's login
        // host" is a fact about the realms table (ADR-0023), and there are only ever a handful of realms.
        var loginRouteIds = await db.Realms.AsNoTracking()
            .Where(r => r.LoginRouteId != null)
            .Select(r => r.LoginRouteId!.Value)
            .ToListAsync(ct);
        var loginRoutes = loginRouteIds.ToHashSet();

        return new Response([.. routes.Select(r => RouteMapping.ToDto(r, loginRoutes.Contains(r.Id)))]);
    }
}
