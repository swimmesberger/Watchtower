using System.Globalization;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Renames an account, edits its contact address, and grants or revokes the Admin role.
/// </summary>
/// <remarks>
/// Deliberately not a password change — that is <c>users.resetPassword</c>, which also revokes the
/// account's sessions. An administrator may demote <em>themselves</em>: the only refusal here is the
/// last-admin guard (<see cref="UserMapping.IsLastUsableAdminAsync"/>), which is what actually
/// protects the instance. Everything else is a legitimate move for someone who already holds the role.
/// </remarks>
[Handler("users.update")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class UpdateUser(
    WatchtowerDbContext db,
    UserManager<User> users,
    IRealmContext realmContext,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<UpdateUser.Command, Result<UpdateUser.Response>> {

    /// <summary>
    /// The realm is deliberately not editable: moving an account between populations is a non-goal in v1
    /// (design.md §13), and it is not a field edit — it would have to carry or invalidate the account's
    /// sessions, grants and group memberships, all of which are realm-scoped.
    /// </summary>
    public sealed record Command(int Id, string UserName, string? Email, bool IsAdmin);

    public sealed record Response(UserDto User);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var userName = command.UserName?.Trim();
        if (string.IsNullOrEmpty(userName))
            return AppError.Validation("User name is required.");

        var user = await users.FindByIdAsync(command.Id.ToString(CultureInfo.InvariantCulture));
        if (user is null)
            return AppError.NotFound($"User {command.Id} not found.");

        if (UserMapping.ValidateAdminRealm(command.IsAdmin, user.RealmId) is { } badRole)
            return badRole;

        // The rename below is validated for uniqueness by Identity, against this account's realm.
        UserMapping.PinRealm(realmContext, user);

        // Evaluated before the entity is mutated, so the query sees the account as it is stored.
        var demoting = user.IsAdmin && !command.IsAdmin;
        if (demoting && await UserMapping.IsLastUsableAdminAsync(db, user, ct))
            return UserMapping.LastAdminError("remove the Admin role from", user);

        var wasAdmin = user.IsAdmin;
        var previousName = user.UserName;

        user.UserName = userName;
        user.Email = UserMapping.NormalizeEmail(command.Email);
        user.IsAdmin = command.IsAdmin;

        // UpdateAsync re-runs Identity's user validator (name shape + uniqueness), refreshes the
        // normalized name, and rotates the concurrency stamp inside the store.
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            return UserMapping.ToError(result);

        // Past the commit point: the account is saved, so the trail is written uncancellably.
        var changes = wasAdmin == command.IsAdmin
            ? $"renamedFrom={previousName}"
            : $"renamedFrom={previousName}; isAdmin={wasAdmin}->{command.IsAdmin}";
        await UserMapping.RecordAsync(db, currentUser, time, AuthEventKinds.UserUpdated, user, changes);

        return new Response(UserMapping.ToDto(user, time.GetUtcNow()));
    }
}
