using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Groups.Handlers;

/// <summary>
/// Removes a group permanently, together with its memberships and every route grant that named it.
/// </summary>
/// <remarks>
/// The memberships and grants go with the row through their cascading foreign keys rather than being
/// deleted by hand first (verified in <c>GroupMemberConfiguration</c> and
/// <c>RouteAccessGrantConfiguration</c>): an explicit pre-delete would be a second uncoordinated write
/// that a failed delete would leave applied, revoking access to routes whose grant still exists.
/// <para>
/// Deleting a group is therefore a revocation as well as a cleanup: members who reached a route only
/// through this group lose it on their next verified request. That is why the count of affected grants is
/// read <em>before</em> the delete and recorded — after the cascade there is nothing left to count.
/// </para>
/// <para>
/// Order is delete-then-audit, the same as <c>DeleteUser</c>: a delete that fails must not leave a trail
/// claiming it happened.
/// </para>
/// </remarks>
[Handler("groups.delete")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class DeleteGroup(WatchtowerDbContext db, ICurrentUser currentUser, TimeProvider time)
    : IHandler<DeleteGroup.Command, Result<DeleteGroup.Response>> {

    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == command.Id, ct);
        if (group is null)
            return AppError.NotFound($"Group {command.Id} not found.");

        var memberCount = await db.GroupMembers.CountAsync(m => m.GroupId == group.Id, ct);
        var grantCount = await db.RouteAccessGrants.CountAsync(g => g.GroupId == group.Id, ct);
        var name = group.Name;

        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);

        // Past the commit point: the group and, by cascade, its memberships and route grants are gone.
        await GroupMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.GroupDeleted, command.Id, name,
            $"members={memberCount}; grantsCascaded={grantCount}");

        return new Response(command.Id);
    }
}
