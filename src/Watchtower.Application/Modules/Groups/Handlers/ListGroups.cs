using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Groups.Handlers;

/// <summary>
/// Lists every group with its member count, ordered by name. The roster an administrator picks from when
/// granting a route to a group.
/// </summary>
[Handler("groups.list")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListGroups(WatchtowerDbContext db)
    : IHandler<ListGroups.Query, Result<ListGroups.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<GroupDto> Groups);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // The count is a correlated subquery rather than a join-and-group: one row per group comes back
        // either way, and this shape keeps groups with no members in the result.
        var groups = await db.Groups.AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.Name, db.GroupMembers.Count(m => m.GroupId == g.Id)))
            .ToListAsync(ct);

        return new Response(groups);
    }
}
