using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Groups.Handlers;

/// <summary>
/// Lists groups with their member counts, ordered by name, optionally narrowed to one realm. The roster
/// an administrator picks from when granting a route to a group.
/// </summary>
/// <remarks>
/// Unfiltered by default, for the same reason <c>users.list</c> is: the caller is an instance
/// administrator (this whole surface is system-realm-only), and a default that hid other populations'
/// groups would hide them from the person responsible for them.
/// </remarks>
[Handler("groups.list")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListGroups(WatchtowerDbContext db)
    : IHandler<ListGroups.Query, Result<ListGroups.Response>> {
    /// <summary>Optional realm filter; omitted means every realm.</summary>
    public sealed record Query(int? RealmId = null);

    public sealed record Response(IReadOnlyList<GroupDto> Groups);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var rows = db.Groups.AsNoTracking().AsQueryable();
        if (query.RealmId is { } realmId) rows = rows.Where(g => g.RealmId == realmId);

        // The count is a correlated subquery rather than a join-and-group: one row per group comes back
        // either way, and this shape keeps groups with no members in the result.
        var groups = await rows
            .OrderBy(g => g.Name)
            .Select(g => new GroupDto(
                g.Id, g.Name, g.RealmId, db.GroupMembers.Count(m => m.GroupId == g.Id)))
            .ToListAsync(ct);

        return new Response(groups);
    }
}
