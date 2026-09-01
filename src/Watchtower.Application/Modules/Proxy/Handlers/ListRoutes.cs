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
        // Domain first, then port, then id. The tie-breaks are not cosmetic: a port route's domain is
        // NULL (ADR-0033), so every one of them ties on the first key, and PostgreSQL is then free to
        // return them in whatever order the heap happens to hold — which the five-minute certificate
        // status write reshuffles, because an update relocates the tuple. Without them the port rows swap
        // places between two polls of the same unchanged table.
        var routes = await db.Routes.AsNoTracking()
            .Include(r => r.Stack)
            .Include(r => r.Realm)
            .OrderBy(r => r.Domain)
            .ThenBy(r => r.ListenPort)
            .ThenBy(r => r.Id)
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
