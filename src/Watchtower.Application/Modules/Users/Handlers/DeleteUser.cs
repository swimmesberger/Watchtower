using System.Globalization;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Removes an account permanently. Subject to the last-admin guard; deleting your own account is
/// otherwise allowed.
/// </summary>
/// <remarks>
/// The order is delete-then-audit, and it matters: Watchtower has no ambient transaction across the two
/// writes (see <see cref="UserMapping.IsLastUsableAdminAsync"/>), so auditing first would mean a delete
/// that then failed on the concurrency stamp left behind a trail claiming an account was removed while
/// it is still there. Writing the row only once the delete has committed can at worst lose an audit row;
/// the other order fabricates one.
/// <para>
/// The audit row therefore carries <c>UserId = null</c> — <c>AuthEvent.UserId</c> is <c>SET NULL</c> on
/// delete, so a new row pointing at a gone account would simply fail the foreign key on insert. The
/// trail outlives its subjects (design.md §3), which is why the name and id live in
/// <see cref="Entities.AuthEvent.Detail"/>.
/// </para>
/// <para>
/// Sessions and route grants go with the row through their cascading foreign keys (verified in
/// <c>AuthSessionConfiguration</c> and <c>RouteAccessGrantConfiguration</c>) rather than being revoked
/// by hand first: an explicit pre-revoke would be a second uncoordinated write that a failed delete
/// would leave applied, signing an account out that still exists. The audit row is this operation's
/// record; the cascade is its mechanism.
/// </para>
/// </remarks>
[Handler("users.delete")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class DeleteUser(
    WatchtowerDbContext db,
    UserManager<User> users,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<DeleteUser.Command, Result<DeleteUser.Response>> {

    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var user = await users.FindByIdAsync(command.Id.ToString(CultureInfo.InvariantCulture));
        if (user is null)
            return AppError.NotFound($"User {command.Id} not found.");

        if (await UserMapping.IsLastUsableAdminAsync(db, user, ct))
            return UserMapping.LastAdminError("delete", user);

        var wasAdmin = user.IsAdmin;
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
            return UserMapping.ToError(result);

        // Past the commit point: the account (and, by cascade, its sessions and grants) is gone.
        await UserMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.UserDeleted, user,
            $"isAdmin={wasAdmin}; sessionsCascaded=true", targetRemoved: true);

        return new Response(command.Id);
    }
}
