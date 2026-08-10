using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Groups.Handlers;

/// <summary>
/// The ids of the accounts in a group. The read side of <see cref="SetGroupMembers"/>; the two share the
/// id-set shape so the members dialog can round-trip what it saved.
/// </summary>
/// <remarks>
/// Ids rather than projected accounts: the caller already has the account roster (it is drawing
/// checkboxes over it), so returning users here would be a second, separately-stale copy of the same list.
/// </remarks>
[Handler("groups.getMembers")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GetGroupMembers(WatchtowerDbContext db)
    : IHandler<GetGroupMembers.Query, Result<GetGroupMembers.Response>> {

    public sealed record Query(int Id);
    public sealed record Response(IReadOnlyList<int> UserIds);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // Existence is answered separately from membership: an unknown group and an empty group are
        // different answers, and returning an empty set for both would let a stale dialog silently save
        // memberships into nothing.
        if (!await db.Groups.AsNoTracking().AnyAsync(g => g.Id == query.Id, ct))
            return AppError.NotFound($"Group {query.Id} not found.");

        var userIds = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == query.Id)
            .Select(m => m.UserId)
            .OrderBy(id => id)
            .ToListAsync(ct);

        return new Response(userIds);
    }
}
