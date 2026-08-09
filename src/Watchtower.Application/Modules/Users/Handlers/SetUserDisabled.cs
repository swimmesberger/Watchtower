using System.Globalization;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Suspends an account or brings it back. Disabling keeps the row (and everything that references
/// it) while denying both login and access verification.
/// </summary>
/// <remarks>
/// Disabling revokes the account's sessions — a suspension that left an open browser tab working
/// would not be a suspension — and is subject to the last-admin guard. Disabling <em>yourself</em> is
/// allowed as long as another administrator can still sign in.
/// <para>
/// Re-enabling also clears the brute-force lockout: an administrator switching an account back on
/// expects a usable account, not one that is still parked behind a lockout window that was running
/// when it was suspended.
/// </para>
/// </remarks>
[Handler("users.setDisabled")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class SetUserDisabled(
    WatchtowerDbContext db,
    UserManager<User> users,
    AuthSessionService sessions,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<SetUserDisabled.Command, Result<SetUserDisabled.Response>> {

    public sealed record Command(int Id, bool Disabled);
    public sealed record Response(UserDto User);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var user = await users.FindByIdAsync(command.Id.ToString(CultureInfo.InvariantCulture));
        if (user is null)
            return AppError.NotFound($"User {command.Id} not found.");

        if (command.Disabled && await UserMapping.IsLastUsableAdminAsync(db, user, ct))
            return UserMapping.LastAdminError("disable", user);

        user.Disabled = command.Disabled;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            return AppError.Validation(UserMapping.Describe(result));

        var detail = string.Empty;
        if (command.Disabled) {
            var revoked = await sessions.RevokeAllForUserAsync(user.Id, ct);
            detail = $"sessionsRevoked={revoked}";
        } else {
            // Both are no-ops when the account was never locked out; calling them unconditionally keeps
            // "enabled" a single, predictable state rather than one that depends on how it was suspended.
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            detail = "lockoutCleared=true";
        }

        var kind = command.Disabled ? "user.disabled" : "user.enabled";
        await UserMapping.RecordAsync(db, currentUser, time, kind, user, detail, ct);

        return new Response(UserMapping.ToDto(user, time.GetUtcNow()));
    }
}
