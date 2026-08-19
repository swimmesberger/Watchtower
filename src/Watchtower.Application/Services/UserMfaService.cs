using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The two-factor operations themselves — enrol, confirm, verify, disable, reissue recovery codes —
/// in one place, so the three surfaces that drive them (the login endpoint, the self-service
/// <c>/api/auth/mfa/*</c> endpoints and the administrative <c>users.resetMfa</c> handler) share one
/// implementation rather than three that agree today.
/// </summary>
/// <remarks>
/// Everything goes through <see cref="UserManager{TUser}"/> rather than touching the entity: that is what
/// makes the security stamp rotate on every change (<see cref="UserManager{TUser}.SetTwoFactorEnabledAsync"/>
/// and <see cref="UserManager{TUser}.ResetAuthenticatorKeyAsync"/> both do it), and the code validation is
/// Identity's own RFC 6238 authenticator provider — Watchtower implements no TOTP arithmetic and takes no
/// package for it.
/// <para>
/// The methods here perform the change and nothing else. Deciding <em>who</em> may ask (the account's own
/// owner, or an administrator) and writing the audit row is the caller's job, because those two answers
/// differ per surface and a service that guessed at them would be wrong for one of the three.
/// </para>
/// </remarks>
public sealed class UserMfaService(WatchtowerDbContext db, UserManager<User> users) {

    /// <summary>
    /// How many recovery codes an enrolment (or a regeneration) issues. Ten is enough that losing the
    /// authenticator is survivable more than once, and few enough that the list stays printable.
    /// </summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>
    /// The label and <c>issuer</c> an authenticator app files the account under. Fixed rather than derived
    /// from the realm: an operator scanning the QR code is enrolling in Watchtower, and a per-realm issuer
    /// would move existing enrolments the moment a realm was renamed.
    /// </summary>
    public const string Issuer = "Watchtower";

    /// <summary>What the account's owner is told about their own two-factor state. Carries no secret.</summary>
    public sealed record MfaStatus(bool TotpEnabled, int RecoveryCodesRemaining);

    /// <summary>
    /// The one-time enrolment payload: the shared secret in both the forms an authenticator app accepts —
    /// scanned, or typed in by hand when there is no camera.
    /// </summary>
    public sealed record TotpEnrolment(string SharedKey, string OtpauthUri);

    /// <summary>Two-factor state of <paramref name="user"/>.</summary>
    public async Task<MfaStatus> GetStatusAsync(User user, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);
        ct.ThrowIfCancellationRequested();
        return new MfaStatus(
            await users.GetTwoFactorEnabledAsync(user),
            await users.CountRecoveryCodesAsync(user));
    }

    /// <summary>
    /// Issues a fresh authenticator key and returns it in the two shapes an app can consume.
    /// </summary>
    /// <remarks>
    /// Two-factor stays <em>off</em> until <see cref="ConfirmTotpAsync"/> proves the app is really set up.
    /// Beginning enrolment therefore cannot lock anyone out, and beginning it twice simply discards the
    /// first key — which is what someone who scanned a code into the wrong app needs to happen.
    /// </remarks>
    public async Task<TotpEnrolment?> BeginTotpAsync(User user, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);
        ct.ThrowIfCancellationRequested();

        var reset = await users.ResetAuthenticatorKeyAsync(user);
        if (!reset.Succeeded) return null;

        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key)) return null;

        return new TotpEnrolment(key, BuildOtpauthUri(user.UserName, key));
    }

    /// <summary>
    /// Verifies a code against the enrolled authenticator key. Used both to finish enrolment and to
    /// authorise the operations that undo it.
    /// </summary>
    public async Task<bool> VerifyTotpAsync(User user, string? code, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code)) return false;
        return await users.VerifyTwoFactorTokenAsync(
            user, TokenOptions.DefaultAuthenticatorProvider, StripWhitespace(code));
    }

    /// <summary>Spends one of the account's recovery codes, reporting whether it was a real one.</summary>
    /// <remarks>
    /// Identity generates codes in a <c>XXXXX-XXXXX</c> shape and the stored hash is of exactly that
    /// string, so the separator cannot simply be stripped the way a TOTP code's spaces can. Instead the
    /// typed value is tried as written and, when it is the ten characters without the dash, once more with
    /// the dash put back — someone retyping from paper should not be punished for omitting punctuation.
    /// </remarks>
    public async Task<bool> RedeemRecoveryCodeAsync(User user, string? code, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code)) return false;

        var typed = StripWhitespace(code).ToUpperInvariant();
        if ((await users.RedeemTwoFactorRecoveryCodeAsync(user, typed)).Succeeded) return true;

        if (typed.Length != 10 || typed.Contains('-', StringComparison.Ordinal)) return false;
        var hyphenated = string.Concat(typed.AsSpan(0, 5), "-", typed.AsSpan(5));
        return (await users.RedeemTwoFactorRecoveryCodeAsync(user, hyphenated)).Succeeded;
    }

    /// <summary>
    /// Turns two-factor on once <paramref name="code"/> proves the authenticator works, and returns the
    /// recovery codes — the only time they exist in readable form.
    /// </summary>
    /// <returns>The codes, or <see langword="null"/> when the code was wrong or no key is enrolled.</returns>
    public async Task<IReadOnlyList<string>?> ConfirmTotpAsync(
        User user, string? code, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);

        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key)) return null;
        if (!await VerifyTotpAsync(user, code, ct)) return null;

        // Enable first: recovery codes are only meaningful for an account that actually demands a second
        // factor, and this is the write that rotates the security stamp.
        var enabled = await users.SetTwoFactorEnabledAsync(user, true);
        if (!enabled.Succeeded) return null;

        return await RegenerateRecoveryCodesAsync(user, ct);
    }

    /// <summary>
    /// Replaces the account's recovery codes with a fresh set and returns it. Whatever was left of the old
    /// set stops working — that is the point: this is what someone whose printed list leaked reaches for.
    /// </summary>
    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(
        User user, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);
        ct.ThrowIfCancellationRequested();
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return codes?.ToArray();
    }

    /// <summary>
    /// Removes the second factor entirely: the flag, the authenticator key and every unused recovery code.
    /// </summary>
    /// <remarks>
    /// All three, always. Clearing the flag while leaving the key behind would let a stale enrolment come
    /// back to life the moment two-factor was switched on again, and leaving spent-set recovery codes on a
    /// disabled account would keep credentials alive that nothing checks the state of.
    /// <para>
    /// The security stamp rotates because <see cref="UserManager{TUser}.SetTwoFactorEnabledAsync"/> rotates
    /// it — losing a factor is a credential change, so anything minted under the old state is stale.
    /// </para>
    /// </remarks>
    public async Task<bool> DisableAsync(User user, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);
        ct.ThrowIfCancellationRequested();

        var disabled = await users.SetTwoFactorEnabledAsync(user, false);
        if (!disabled.Succeeded) return false;

        // The key has no UserManager "clear" — SetAuthenticatorKeyAsync takes a non-null value — so it is
        // nulled on the tracked entity and persisted through the same store write path everything else uses.
        user.AuthenticatorKey = null;
        var cleared = await users.UpdateAsync(user);
        if (!cleared.Succeeded) return false;

        await db.UserRecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(ct);
        return true;
    }

    /// <summary>
    /// The <c>otpauth://</c> URI an authenticator app expects behind a QR code
    /// (<c>otpauth://totp/Watchtower:{user}?secret=…&amp;issuer=Watchtower</c>).
    /// </summary>
    /// <remarks>
    /// Both the label and the <c>issuer</c> parameter carry the issuer, as the key-URI format requires:
    /// apps that predate the parameter read the label prefix, and the rest read the parameter. The user
    /// name is escaped because it is operator-supplied text going into a URI.
    /// </remarks>
    public static string BuildOtpauthUri(string userName, string sharedKey) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            Uri.EscapeDataString(Issuer),
            Uri.EscapeDataString(userName ?? string.Empty),
            Uri.EscapeDataString(sharedKey ?? string.Empty));

    /// <summary>
    /// Removes every space in a code. Authenticator apps display six digits as "123 456" and a copy-paste
    /// brings the space along; refusing that would only ever punish someone for copying what they were
    /// shown.
    /// </summary>
    private static string StripWhitespace(string code) =>
        string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
}
