using System.Globalization;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Clears an account's two-factor enrolment on an administrator's say-so: the flag, the authenticator key
/// and every unused recovery code.
/// </summary>
/// <remarks>
/// The recovery path for the mishap the recovery codes themselves do not cover — a phone lost together
/// with the printed list, or an enrolment that went into an app on a device nobody has any more. Without
/// it the only way back into such an account would be to delete and recreate it, losing its group
/// memberships and route grants along with its password.
/// <para>
/// Deliberately one-directional: an administrator can take a second factor <em>away</em> and can never put
/// one on. Enrolment requires proving possession of the authenticator (<c>/api/auth/mfa/totp/confirm</c>),
/// which only the account's own owner can do — so there is no path here through which someone else's
/// account could be given a second factor they do not hold.
/// </para>
/// <para>
/// The security stamp rotates as part of the change, because
/// <see cref="UserManager{TUser}.SetTwoFactorEnabledAsync"/> rotates it. That is bookkeeping and nothing
/// more today: session validation never compares the stamp (see <see cref="User.SecurityStamp"/>), so no
/// session ends as a result. Nor should one — this operation exists because someone <em>cannot</em> get in,
/// and signing out whatever sessions they might still hold would work against that.
/// </para>
/// </remarks>
[Handler("users.resetMfa")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ResetUserMfa(
    WatchtowerDbContext db,
    UserManager<User> users,
    UserMfaService mfa,
    IRealmContext realmContext,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<ResetUserMfa.Command, Result<ResetUserMfa.Response>> {

    public sealed record Command(int Id);

    /// <summary>
    /// <paramref name="WasEnabled"/> reports what the reset actually undid, so an administrator clearing an
    /// account that had nothing enrolled learns that rather than being told a factor was removed.
    /// </summary>
    public sealed record Response(int Id, bool WasEnabled);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var user = await users.FindByIdAsync(command.Id.ToString(CultureInfo.InvariantCulture));
        if (user is null)
            return AppError.NotFound($"User {command.Id} not found.");

        // The writes below run through UserManager, which re-runs Identity's realm-scoped duplicate-name
        // check; without this the question would be asked of the operator population instead of this
        // account's own (docs/central-auth/design.md §13).
        UserMapping.PinRealm(realmContext, user);

        var wasEnabled = user.TwoFactorEnabled;
        var hadKey = user.AuthenticatorKey is not null;
        var codesDropped = await users.CountRecoveryCodesAsync(user);

        // Unconditional, even when nothing was enrolled: it also rotates the security stamp, and an
        // administrator asking for a clean slate should get one rather than a no-op that depends on what
        // the row happened to contain.
        if (!await mfa.DisableAsync(user, ct))
            return AppError.Conflict(
                $"Two-factor authentication for '{user.UserName}' could not be reset. " +
                "Another administrator may have changed the account; re-read it and try again.");

        await UserMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.MfaTotpReset, user,
            $"wasEnabled={wasEnabled}; hadKey={hadKey}; recoveryCodesDropped={codesDropped}");

        return new Response(user.Id, wasEnabled);
    }
}
