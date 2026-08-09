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
/// The audit row is written <em>before</em> the delete and names the account in
/// <see cref="Entities.AuthEvent.Detail"/>, because <c>AuthEvent.UserId</c> is <c>SET NULL</c> when
/// the user goes: the trail outlives its subjects (design.md §3), so the identity has to be in the text.
/// <para>
/// Sessions and route grants would go with the row anyway — both foreign keys cascade — but the
/// sessions are revoked explicitly first so that signing the account out is an operation this handler
/// performs and can report, rather than a database side effect that a future change to a delete
/// behaviour could silently remove.
/// </para>
/// </remarks>
[Handler("users.delete")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class DeleteUser(
    WatchtowerDbContext db,
    UserManager<User> users,
    AuthSessionService sessions,
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

        var revoked = await sessions.RevokeAllForUserAsync(user.Id, ct);
        await UserMapping.RecordAsync(
            db, currentUser, time, "user.deleted", user, $"sessionsRevoked={revoked}", ct);

        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
            return AppError.Conflict(UserMapping.Describe(result));

        return new Response(command.Id);
    }
}
