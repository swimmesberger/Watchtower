using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Reads a route's access policy (docs/central-auth/design.md §7): its <see cref="AccessMode"/>, the
/// newline-separated bypass paths, and — for a <see cref="AccessMode.Restricted"/> route — the ids of the
/// users and groups granted through it. The read side of <see cref="SetAccess"/>; the two share a response
/// shape so a form can round-trip what it saved.
/// </summary>
/// <remarks>
/// Deciding who may reach an app is an administrative act, so this carries the same
/// <c>[RequireRole("Admin")]</c> the Users module does — a non-administrator has no business enumerating a
/// route's grant list. The granted ids are returned whatever the mode, not only for <c>Restricted</c>: a
/// form that has just switched a route away from <c>Restricted</c> still wants to show the selection it
/// would restore, and <see cref="SetAccess"/> is what actually clears the rows.
/// </remarks>
[Handler("proxy.getAccess")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GetAccess(WatchtowerDbContext db)
    : IHandler<GetAccess.Query, Result<GetAccess.Response>> {
    public sealed record Query(int RouteId);
    public sealed record Response(
        AccessMode Mode,
        IdentityHeaderMode IdentityHeaderMode,
        string? BypassPaths,
        IReadOnlyList<int> GrantedUserIds,
        IReadOnlyList<int> GrantedGroupIds,
        // The population the route's grants may name (docs/central-auth/design.md §13). Not policy, which is
        // why it comes last: it is the context a grant editor needs to offer only candidates SetAccess would
        // accept, so the cross-realm refusal is something the caller never has to run into.
        int RealmId);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var route = await db.Routes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == query.RouteId, ct);
        if (route is null)
            return AppError.NotFound($"Route {query.RouteId} not found");

        // Resolved the same way SetAccess resolves it — the stack's category, or the operator realm for a
        // standalone stack — so the read and the write agree on which population a grant may come from.
        var realmId = await RouteAccessPolicy.RouteRealmIdAsync(db, route.Id, ct);
        if (realmId is null)
            return AppError.NotFound($"Route {query.RouteId} not found");

        // One read for both subject kinds, split afterwards: a grant row carries exactly one of the two
        // (the CHECK constraint on the table says so), so the split is total and loses nothing.
        var grants = await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.RouteId == route.Id)
            .Select(g => new { g.UserId, g.GroupId })
            .ToListAsync(ct);

        return new Response(
            route.AccessMode,
            route.IdentityHeaderMode,
            route.BypassPaths,
            [.. grants.Where(g => g.UserId is not null).Select(g => g.UserId!.Value).Order()],
            [.. grants.Where(g => g.GroupId is not null).Select(g => g.GroupId!.Value).Order()],
            realmId.Value);
    }
}
