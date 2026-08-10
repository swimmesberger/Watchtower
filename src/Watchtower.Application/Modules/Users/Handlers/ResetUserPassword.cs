using System.Globalization;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Sets a new password for an account and signs it out everywhere.
/// </summary>
/// <remarks>
/// The reset goes through a password-reset token rather than a direct hash write, exactly as
/// <see cref="AuthBootstrapService"/>'s break-glass path does:
/// <see cref="UserManager{TUser}.ResetPasswordAsync"/> runs the password validators
/// <em>before</em> touching the stored hash, so a policy-violating value leaves the previous
/// password working instead of clearing it and locking the account's owner out.
/// <para>
/// Revoking the account's sessions is part of the operation, not a follow-up: an administrator
/// resetting someone's password is acting because control of the account is in doubt, and a session
/// minted before the reset would otherwise keep working for up to the absolute session lifetime.
/// Resetting your own password therefore signs you out too — which is the honest behaviour. Because it
/// is part of the operation, everything after the password commits runs on
/// <see cref="CancellationToken.None"/>: a caller that hangs up mid-request must not be able to leave
/// the account with a new password and its old sessions still live.
/// </para>
/// </remarks>
[Handler("users.resetPassword")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ResetUserPassword(
    WatchtowerDbContext db,
    UserManager<User> users,
    AuthSessionService sessions,
    IRealmContext realmContext,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<ResetUserPassword.Command, Result<ResetUserPassword.Response>> {

    public sealed record Command(int Id, string NewPassword);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (string.IsNullOrEmpty(command.NewPassword))
            return AppError.Validation("Password is required.");

        var user = await users.FindByIdAsync(command.Id.ToString(CultureInfo.InvariantCulture));
        if (user is null)
            return AppError.NotFound($"User {command.Id} not found.");

        // The lockout clear below writes the account back through UserManager, which re-runs the
        // duplicate-name check against whichever realm this scope is pointed at.
        UserMapping.PinRealm(realmContext, user);

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, command.NewPassword);
        if (!result.Succeeded)
            return UserMapping.ToError(result);

        // --- Past the commit point. The new password is live from here on, so the rest must not be
        // --- abandoned because the caller's HTTP connection went away.

        // The reset also clears whatever brute-force lockout the old password accumulated — otherwise the
        // account the administrator just fixed stays unusable until the timer lapses. One write, and its
        // result is checked: a concurrency failure here means someone else edited the account between the
        // read and now, and the caller needs to know the lockout was NOT cleared (the password was).
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        var cleared = await users.UpdateAsync(user);
        if (!cleared.Succeeded)
            return UserMapping.ToError(cleared);

        var revoked = await sessions.RevokeAllForUserAsync(user.Id, CancellationToken.None);

        await UserMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.UserPasswordReset, user,
            $"sessionsRevoked={revoked}; lockoutCleared=true");

        return new Response(user.Id);
    }
}
