using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Watchtower.Api.Authentication;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The two-factor half of the login surface: the step that finishes a challenged login
/// (<c>POST /api/auth/login/mfa</c>) and the self-service enrolment endpoints under
/// <c>/api/auth/mfa/*</c>.
/// </summary>
/// <remarks>
/// Plain HTTP endpoints rather than JSON-RPC handlers, for two different reasons. The login step must work
/// for a caller that holds no session at all — that is the whole point of it. The self-service endpoints
/// could be handlers, but every management handler in this application is gated to the operator realm
/// (<see cref="SystemRealmAuthorizer"/>), and protecting your own account is not management: a customer
/// realm's account must be able to turn on two-factor for itself, so the surface has to sit outside that
/// gate. Both operate on the caller's <em>own</em> account and take no user id — there is no parameter
/// through which one account could reach another.
/// </remarks>
public static partial class WatchtowerAuthEndpoints {
    /// <summary>
    /// The second factor, finishing the login the password started. Exactly one of
    /// <paramref name="Code"/> (an authenticator's six digits) and <paramref name="RecoveryCode"/> is
    /// expected; <paramref name="RedirectUri"/> carries the cross-domain hand-over target through the
    /// challenge, exactly as the password step's does.
    /// </summary>
    public sealed record MfaLoginRequest(
        string? MfaToken, string? Code, string? RecoveryCode, string? RedirectUri);

    /// <summary>The account's own view of its two-factor state. Deliberately carries no key and no codes.</summary>
    public sealed record MfaStatusResponse(bool TotpEnabled, int RecoveryCodesRemaining);

    /// <summary>
    /// The enrolment secret, in the two forms an authenticator app takes it: scanned from
    /// <paramref name="OtpauthUri"/>, or typed from <paramref name="SharedKey"/> when there is no camera.
    /// This is the one response in the whole API that carries the authenticator key, it goes only to the
    /// account's own owner, and it is never repeatable — asking again mints a different key.
    /// </summary>
    public sealed record MfaBeginResponse(string SharedKey, string OtpauthUri);

    /// <summary>A code the caller is proving something with — an authenticator's, or a recovery code.</summary>
    public sealed record MfaCodeRequest(string? Code);

    /// <summary>
    /// A freshly issued set of recovery codes. Returned exactly once per generation: only their hashes are
    /// kept, so nothing can ever show them again.
    /// </summary>
    public sealed record MfaRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

    /// <summary>
    /// The single failure body every rejected second factor gets — wrong code, spent recovery code,
    /// unknown challenge and expired challenge alike.
    /// </summary>
    /// <remarks>
    /// Distinguishing them would tell a caller holding a stolen password whether the challenge is still
    /// alive and worth grinding, which is precisely the thing not to say. The <em>client</em> still
    /// recovers sensibly from an expired token, because it knows how long ago it asked.
    /// </remarks>
    private static readonly AuthErrorResponse InvalidSecondFactor =
        new("That code is not valid. Please try again.");

    /// <summary>Refusal for a caller with no live central session.</summary>
    private static readonly AuthErrorResponse NotSignedIn = new("You are not signed in.");

    /// <summary>Refusal for an enrolment attempt on an account that already has one.</summary>
    private static readonly AuthErrorResponse AlreadyEnrolled =
        new("Two-factor authentication is already enabled. Turn it off before enrolling a new authenticator.");

    /// <summary>Refusal for the operations that only mean something once an authenticator is enrolled.</summary>
    private static readonly AuthErrorResponse NotEnrolled =
        new("Two-factor authentication is not enabled for this account.");

    /// <summary>404 stubs for the MFA routes when central authorization is switched off, like the login ones.</summary>
    private static void MapMfaNotFound(WebApplication app) {
        app.MapGet("/api/auth/mfa", () => Results.NotFound());
        app.MapPost("/api/auth/mfa/totp/begin", () => Results.NotFound());
        app.MapPost("/api/auth/mfa/totp/confirm", () => Results.NotFound());
        app.MapPost("/api/auth/mfa/totp/disable", () => Results.NotFound());
        app.MapPost("/api/auth/mfa/recovery/regenerate", () => Results.NotFound());
    }

    /// <summary>
    /// Finishes a challenged login. Succeeds into exactly the same state a single-factor login produces —
    /// the same <c>__wt_sso</c> cookie, the same body, the same <c>continueUrl</c> hand-over — so nothing
    /// downstream has to know which of the two paths a session came from.
    /// </summary>
    /// <remarks>
    /// A wrong code leaves the pending record alone until it expires (one mistyped digit must not send the
    /// visitor back to the password form) but does count against the account lockout, so the five-minute
    /// window is not a free brute-force gallery — five wrong codes park the account exactly as five wrong
    /// passwords do. The per-IP login limiter covers the route as well, for the same reason it covers the
    /// password step.
    /// </remarks>
    private static void MapMfaLogin(WebApplication app) {
        app.MapPost("/api/auth/login/mfa", async (
            HttpContext http,
            UserManager<User> users,
            UserMfaService mfa,
            AuthSessionService sessions,
            IOptionsMonitor<WatchtowerOptions> options,
            WatchtowerDbContext db,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            // Same CSRF reasoning as the password step: a cross-site HTML form cannot produce this content
            // type, so a forged POST cannot complete somebody else's challenge.
            if (!http.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            MfaLoginRequest? body;
            try {
                body = await http.Request.ReadFromJsonAsync<MfaLoginRequest>(ct);
            } catch (System.Text.Json.JsonException) {
                return Results.BadRequest();
            }

            var pending = await sessions.FindMfaPendingAsync(body?.MfaToken, ct);
            if (pending?.User is null)
                return await RejectSecondFactorAsync(db, time, null, "unknown or expired challenge", http);

            var user = pending.User;
            // Every UserManager write below re-runs Identity's duplicate-name check through the store, and
            // that lookup is realm-scoped (design.md §13) — so point the scope at this account's realm
            // before touching it, exactly as the password step pins the realm of the login host.
            realmContext.SetRealm(user.RealmId);

            // A lockout that landed between the password and the code still refuses, and still without
            // being extended: the same rule the password step applies.
            if (await users.IsLockedOutAsync(user))
                return await RejectSecondFactorAsync(db, time, user.Id, "account locked out", http);

            var usingRecoveryCode = !string.IsNullOrWhiteSpace(body?.RecoveryCode);
            var accepted = usingRecoveryCode
                ? await mfa.RedeemRecoveryCodeAsync(user, body!.RecoveryCode, ct)
                : await mfa.VerifyTotpAsync(user, body?.Code, ct);

            if (!accepted) {
                // Counted, so the lockout policy governs the second factor as well as the first.
                await users.AccessFailedAsync(user);
                return await RejectSecondFactorAsync(
                    db, time, user.Id, usingRecoveryCode ? "bad recovery code" : "bad code", http);
            }

            // The delete is the claim: two requests that both present a correct code for one challenge
            // produce one session, not two.
            if (!await sessions.ConsumeMfaPendingAsync(pending.Id, ct))
                return await RejectSecondFactorAsync(db, time, user.Id, "challenge already used", http);

            if (await users.GetAccessFailedCountAsync(user) > 0)
                await users.ResetAccessFailedCountAsync(user);

            var token = await sessions.CreateSsoSessionAsync(user, ct);
            AuthCookies.Append(
                http, AuthSessionService.SsoCookieName, token,
                sessions.AbsoluteLifetime, options.CurrentValue.Auth.CookieSecure);

            // One row for the login, not a login.ok plus this: a two-factor sign-in is a single event, and
            // a trail that also claimed a password-only success would misdescribe it.
            Record(db, time, AuthEventKinds.LoginMfaOk, user.Id, Describe(http, reason: null));
            if (usingRecoveryCode) {
                var remaining = await users.CountRecoveryCodesAsync(user);
                Record(
                    db, time, AuthEventKinds.MfaRecoveryRedeemed, user.Id,
                    Describe(http, $"remaining={remaining}"));
            }
            // The session exists; the rows recording it must not depend on the client staying connected.
            await db.SaveChangesAsync(CancellationToken.None);

            if (string.IsNullOrWhiteSpace(body?.RedirectUri))
                return Results.Ok(new LoginResponse(user.UserName, user.IsAdmin));

            var continueUrl = await IssueContinueUrlAsync(
                db, sessions, time, http, user.Id, body.RedirectUri, ct);
            return continueUrl is null
                ? Results.Json(AccessNotPermitted, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(new LoginResponse(user.UserName, user.IsAdmin, continueUrl));
        })
        // Same per-IP backstop as the password step (design.md §9): without it the pending window would be
        // the one login-adjacent route an attacker could hammer freely.
        .RequireRateLimiting(LoginRateLimiting.PolicyName);
    }

    /// <summary>Maps the five self-service routes. All operate on the caller's own account, and only on it.</summary>
    private static void MapMfaSelfService(WebApplication app) {
        // ── Status ──────────────────────────────────────────────────────────
        app.MapGet("/api/auth/mfa", async (
            HttpContext http,
            AuthSessionService sessions,
            UserMfaService mfa,
            IRealmContext realmContext,
            CancellationToken ct) => {

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            var status = await mfa.GetStatusAsync(user, ct);
            return Results.Ok(new MfaStatusResponse(status.TotpEnabled, status.RecoveryCodesRemaining));
        });

        // ── Begin enrolment ─────────────────────────────────────────────────
        // Refused while two-factor is already on, and that is a safety rule rather than tidiness: minting a
        // new key for an enabled account would immediately invalidate the authenticator the owner is
        // actually using, and since nothing confirms the new one they would be locked out of their own
        // account by a request that never asked them for anything. Re-enrolling means disabling first,
        // which costs a code the owner must still be able to produce.
        app.MapPost("/api/auth/mfa/totp/begin", async (
            HttpContext http,
            AuthSessionService sessions,
            UserMfaService mfa,
            IRealmContext realmContext,
            CancellationToken ct) => {

            if (!http.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            if (user.TwoFactorEnabled)
                return Results.Json(AlreadyEnrolled, statusCode: StatusCodes.Status409Conflict);

            var enrolment = await mfa.BeginTotpAsync(user, ct);
            return enrolment is null
                ? Results.StatusCode(StatusCodes.Status500InternalServerError)
                : Results.Ok(new MfaBeginResponse(enrolment.SharedKey, enrolment.OtpauthUri));
        });

        // ── Confirm enrolment ───────────────────────────────────────────────
        app.MapPost("/api/auth/mfa/totp/confirm", async (
            HttpContext http,
            AuthSessionService sessions,
            UserMfaService mfa,
            WatchtowerDbContext db,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            var request = await ReadCodeAsync(http, ct);
            if (request.Failure is not null) return request.Failure;

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            if (user.TwoFactorEnabled)
                return Results.Json(AlreadyEnrolled, statusCode: StatusCodes.Status409Conflict);

            var codes = await mfa.ConfirmTotpAsync(user, request.Code, ct);
            if (codes is null)
                return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);

            Record(db, time, AuthEventKinds.MfaTotpEnabled, user.Id, Describe(http, reason: null));
            Record(
                db, time, AuthEventKinds.MfaRecoveryGenerated, user.Id,
                Describe(http, $"count={codes.Count}"));
            await db.SaveChangesAsync(CancellationToken.None);

            // The one moment the codes are readable. Nothing can show them again.
            return Results.Ok(new MfaRecoveryCodesResponse(codes));
        });

        // ── Disable ─────────────────────────────────────────────────────────
        // A recovery code is accepted here as well as an authenticator code, deliberately: someone whose
        // phone is gone needs a way to switch two-factor off from a session they still hold, and the
        // alternative would be to make an administrator the only route out of an ordinary mishap.
        app.MapPost("/api/auth/mfa/totp/disable", async (
            HttpContext http,
            AuthSessionService sessions,
            UserMfaService mfa,
            WatchtowerDbContext db,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            var request = await ReadCodeAsync(http, ct);
            if (request.Failure is not null) return request.Failure;

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            if (!user.TwoFactorEnabled)
                return Results.Json(NotEnrolled, statusCode: StatusCodes.Status409Conflict);

            var byRecoveryCode = false;
            if (!await mfa.VerifyTotpAsync(user, request.Code, ct)) {
                byRecoveryCode = await mfa.RedeemRecoveryCodeAsync(user, request.Code, ct);
                if (!byRecoveryCode)
                    return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!await mfa.DisableAsync(user, ct))
                return Results.Json(
                    new AuthErrorResponse("Two-factor authentication could not be turned off. Please try again."),
                    statusCode: StatusCodes.Status409Conflict);

            Record(
                db, time, AuthEventKinds.MfaTotpDisabled, user.Id,
                Describe(http, byRecoveryCode ? "authorised by recovery code" : null));
            await db.SaveChangesAsync(CancellationToken.None);

            return Results.Ok(new MfaStatusResponse(TotpEnabled: false, RecoveryCodesRemaining: 0));
        });

        // ── Reissue recovery codes ──────────────────────────────────────────
        // An authenticator code only — no recovery code. Spending one recovery code to mint ten fresh ones
        // would turn a single leaked code into permanent access; proving current possession of the
        // authenticator is the whole point of the check.
        app.MapPost("/api/auth/mfa/recovery/regenerate", async (
            HttpContext http,
            AuthSessionService sessions,
            UserMfaService mfa,
            WatchtowerDbContext db,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            var request = await ReadCodeAsync(http, ct);
            if (request.Failure is not null) return request.Failure;

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            if (!user.TwoFactorEnabled)
                return Results.Json(NotEnrolled, statusCode: StatusCodes.Status409Conflict);

            if (!await mfa.VerifyTotpAsync(user, request.Code, ct))
                return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);

            var codes = await mfa.RegenerateRecoveryCodesAsync(user, ct);
            if (codes is null)
                return Results.StatusCode(StatusCodes.Status500InternalServerError);

            Record(
                db, time, AuthEventKinds.MfaRecoveryGenerated, user.Id,
                Describe(http, $"count={codes.Count}; replaced"));
            await db.SaveChangesAsync(CancellationToken.None);

            return Results.Ok(new MfaRecoveryCodesResponse(codes));
        });
    }

    /// <summary>
    /// The account behind the caller's <c>__wt_sso</c> cookie, with the request scope pointed at its realm,
    /// or <see langword="null"/> when there is no live central session.
    /// </summary>
    /// <remarks>
    /// The session row is the authority, the same way <c>/api/auth/continue</c> resolves its caller — these
    /// routes carry no ASP.NET authorization metadata, so nothing else would have established who is
    /// asking. Pinning the realm matters for the same reason it does on the login path: every
    /// <c>UserManager</c> write re-runs a realm-scoped duplicate-name lookup, and against the wrong
    /// population an unrelated account with the same name reads as a duplicate of this one.
    /// </remarks>
    private static async Task<User?> CurrentAccountAsync(
        HttpContext http, AuthSessionService sessions, IRealmContext realmContext, CancellationToken ct) {
        var session = await sessions.ValidateAsync(
            http.Request.Cookies[AuthSessionService.SsoCookieName], ct);
        if (session?.User is null) return null;
        realmContext.SetRealm(session.User.RealmId);
        return session.User;
    }

    /// <summary>The one refusal an unauthenticated caller of the self-service surface gets.</summary>
    private static IResult Unauthenticated() =>
        Results.Json(NotSignedIn, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>A parsed <c>{ code }</c> body, or the response that should be returned instead of reading one.</summary>
    private readonly record struct CodeRequest(string? Code, IResult? Failure);

    /// <summary>
    /// Reads the <c>{ code }</c> body every proving endpoint takes, enforcing the JSON content type first.
    /// </summary>
    /// <remarks>
    /// The content-type requirement is the CSRF control the whole file relies on (design.md §9): a
    /// cross-site HTML form can only send "simple" content types, so refusing everything else is what keeps
    /// a forged POST from switching somebody's second factor off behind their back.
    /// </remarks>
    private static async Task<CodeRequest> ReadCodeAsync(HttpContext http, CancellationToken ct) {
        if (!http.Request.HasJsonContentType())
            return new CodeRequest(null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        try {
            var body = await http.Request.ReadFromJsonAsync<MfaCodeRequest>(ct);
            return new CodeRequest(body?.Code, null);
        } catch (System.Text.Json.JsonException) {
            return new CodeRequest(null, Results.BadRequest());
        }
    }

    /// <summary>
    /// Records a rejected second factor and answers with the one indistinguishable refusal. The audit write
    /// ignores <see cref="HttpContext.RequestAborted"/> for the same reason the password step's does: a
    /// caller that hangs up mid-attempt must not be able to keep its failures out of the trail.
    /// </summary>
    private static async Task<IResult> RejectSecondFactorAsync(
        WatchtowerDbContext db, TimeProvider time, int? userId, string reason, HttpContext http) {
        Record(db, time, AuthEventKinds.LoginMfaFailed, userId, Describe(http, reason));
        await db.SaveChangesAsync(CancellationToken.None);
        return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);
    }
}
