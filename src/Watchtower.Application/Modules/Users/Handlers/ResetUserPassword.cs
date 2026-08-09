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
/// Resetting your own password therefore signs you out too — which is the honest behaviour.
/// </para>
/// </remarks>
[Handler("users.resetPassword")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ResetUserPassword(
    WatchtowerDbContext db,
    UserManager<User> users,
    AuthSessionService sessions,
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

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, command.NewPassword);
        if (!result.Succeeded)
            return AppError.Validation(UserMapping.Describe(result));

        // A password reset also clears whatever brute-force lockout the old password accumulated —
        // otherwise the account the administrator just fixed stays unusable until the timer lapses.
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);

        var revoked = await sessions.RevokeAllForUserAsync(user.Id, ct);

        await UserMapping.RecordAsync(
            db, currentUser, time, "user.password.reset", user, $"sessionsRevoked={revoked}", ct);

        return new Response(user.Id);
    }
}
