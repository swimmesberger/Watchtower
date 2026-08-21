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
    /// Finishing enrolment takes both: the <paramref name="Code"/> proves the new authenticator works, and
    /// the account <paramref name="Password"/> proves the request comes from the account's owner rather
    /// than from whoever is holding its session.
    /// </summary>
    public sealed record MfaConfirmRequest(string? Code, string? Password);

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

    /// <summary>
    /// Answer for an enrolment that failed on a write rather than on the caller's input. Distinct from
    /// <see cref="InvalidSecondFactor"/> because the remedy is different: retrying the same code is the
    /// right thing to do, and being told the code was wrong would send someone to re-scan a working key.
    /// </summary>
    private static readonly AuthErrorResponse EnrolmentFailed =
        new("Two-factor authentication could not be turned on. Nothing was changed — please try again.");

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
            RealmResolver realms,
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

            // Which population this request belongs to, decided by the host it arrived on — exactly as the
            // password step decides it (design.md §13). Resolved before the challenge is looked at so the
            // two halves of one login are answered by one realm and not two.
            var hostRealm = await realms.ResolveByHostAsync(http.Request.Host.Host, ct);
            realmContext.SetRealm(hostRealm.Id);

            var pending = await sessions.FindMfaPendingAsync(body?.MfaToken, ct);
            if (pending?.User is null)
                return await RejectSecondFactorAsync(db, time, null, "unknown or expired challenge", http);

            var user = pending.User;

            // A challenge is redeemable only on the login host of the realm that issued it. Without this a
            // pending token minted on realm A's host would finish on realm B's, and the __wt_sso cookie
            // would be set on B's host for an account that does not exist in B's population — the cookie
            // jar and the credential space are supposed to be the same boundary, and this is where that
            // could come apart. Refused as the same generic 401 as a wrong code: which realm an account
            // lives in is not something a caller gets to probe.
            if (user.RealmId != hostRealm.Id)
                return await RejectSecondFactorAsync(db, time, user.Id, "wrong realm host", http);

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

            // Verify first, then consume — and the order is a decision, not an accident.
            //
            // Consuming up front would make the challenge strictly single-attempt: every mistyped digit
            // would burn it and send the visitor back to the password form. Verifying first means the
            // challenge survives a typo, at the cost of one race: two requests carrying the *same correct*
            // code can both pass verification before either deletes the row. The delete is what settles it
            // — its affected-row count is the claim, so exactly one of them mints a session and the other
            // gets the generic refusal below. What the loser can cost is one recovery code, since
            // RedeemCodeAsync has already spent it by then.
            //
            // That is the whole exposure: a double-submitted recovery code can consume two codes and yield
            // one session. Weighed against a full re-login on every typo — which is the common case, not
            // the rare one — it is the better trade. Brute force is not what is being bounded here anyway;
            // the lockout above and the five-minute window are.
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
            await AuthAudit.QueueAsync(db, time, AuthEventKinds.LoginMfaOk, user.Id, null, Describe(http, reason: null));
            if (usingRecoveryCode) {
                var remaining = await users.CountRecoveryCodesAsync(user);
                await AuthAudit.QueueAsync(
                    db, time, AuthEventKinds.MfaRecoveryRedeemed, user.Id, null,
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
    /// <remarks>
    /// Every route that judges a code carries the login rate limiter and drives the account lockout on a
    /// refusal, exactly as the two login steps do. Holding a session is not a reason to relax either: a
    /// stolen or borrowed session is precisely the case where an attacker would sit and grind six digits to
    /// turn the second factor off, and without the limiter this surface would be the one login-adjacent
    /// place they could do it freely. The limiter partitions by client address under the same
    /// <c>login:{ip}</c> key as the login routes, so a burst here also spends the login budget — deliberate,
    /// since both are the same client trying credentials at the same instance.
    /// <para>
    /// The status read is deliberately unlimited: it judges nothing, returns no secret, and a page that
    /// could not tell you whether two-factor is on would be worse than useless during exactly the incident
    /// the limiter exists for.
    /// </para>
    /// </remarks>
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
        })
        // Judges nothing, but it does mint a secret and it is the entry point to the flow — limited for
        // symmetry, so no route in this group is the cheap one to hammer.
        .RequireRateLimiting(LoginRateLimiting.PolicyName);

        // ── Confirm enrolment ───────────────────────────────────────────────
        // Takes the account password as well as the code. The code proves possession of the new
        // authenticator, which is exactly what an attacker holding a borrowed session would have — they
        // enrol *their* app and inherit the account permanently. The password is the one thing that
        // session does not carry, so it is what makes enrolment an act of the account's owner rather than
        // of whoever is holding the cookie. Mirrors the reason begin is refused while two-factor is on:
        // both keep control of the second factor with the person who holds the first.
        app.MapPost("/api/auth/mfa/totp/confirm", async (
            HttpContext http,
            UserManager<User> users,
            AuthSessionService sessions,
            UserMfaService mfa,
            WatchtowerDbContext db,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            var request = await ReadJsonAsync<MfaConfirmRequest>(http, ct);
            if (request.Failure is not null) return request.Failure;

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            if (user.TwoFactorEnabled)
                return Results.Json(AlreadyEnrolled, statusCode: StatusCodes.Status409Conflict);

            // Lockout first, so a parked account cannot be ground at through this route either.
            if (await users.IsLockedOutAsync(user))
                return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);

            var password = request.Body?.Password;
            if (string.IsNullOrEmpty(password) || !await users.CheckPasswordAsync(user, password))
                return await RefuseAsync(users, user);

            var confirmed = await mfa.ConfirmTotpAsync(user, request.Body?.Code, ct);
            if (confirmed.Outcome == UserMfaService.ConfirmOutcome.RejectedCode)
                return await RefuseAsync(users, user);
            if (confirmed.Outcome != UserMfaService.ConfirmOutcome.Enabled || confirmed.Codes is null) {
                // A write failed and the flag was rolled back. Not the caller's input, so not a 401 —
                // sending them to retype digits that were never the problem is the wrong answer.
                return Results.Json(EnrolmentFailed, statusCode: StatusCodes.Status500InternalServerError);
            }

            var codes = confirmed.Codes;
            if (await users.GetAccessFailedCountAsync(user) > 0)
                await users.ResetAccessFailedCountAsync(user);

            await AuthAudit.QueueAsync(db, time, AuthEventKinds.MfaTotpEnabled, user.Id, null, Describe(http, reason: null));
            await AuthAudit.QueueAsync(
                db, time, AuthEventKinds.MfaRecoveryGenerated, user.Id, null,
                Describe(http, $"count={codes.Count}"));
            await db.SaveChangesAsync(CancellationToken.None);

            // The one moment the codes are readable. Nothing can show them again.
            return Results.Ok(new MfaRecoveryCodesResponse(codes));
        })
        .RequireRateLimiting(LoginRateLimiting.PolicyName);

        // ── Disable ─────────────────────────────────────────────────────────
        // A recovery code is accepted here as well as an authenticator code, deliberately: someone whose
        // phone is gone needs a way to switch two-factor off from a session they still hold, and the
        // alternative would be to make an administrator the only route out of an ordinary mishap.
        app.MapPost("/api/auth/mfa/totp/disable", async (
            HttpContext http,
            UserManager<User> users,
            AuthSessionService sessions,
            UserMfaService mfa,
            WatchtowerDbContext db,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            var request = await ReadJsonAsync<MfaCodeRequest>(http, ct);
            if (request.Failure is not null) return request.Failure;

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            if (!user.TwoFactorEnabled)
                return Results.Json(NotEnrolled, statusCode: StatusCodes.Status409Conflict);

            if (await users.IsLockedOutAsync(user))
                return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);

            var byRecoveryCode = false;
            if (!await mfa.VerifyTotpAsync(user, request.Body?.Code, ct)) {
                byRecoveryCode = await mfa.RedeemRecoveryCodeAsync(user, request.Body?.Code, ct);
                // Counted: turning the second factor off is the outcome an attacker on a borrowed session
                // wants most, so guesses at it cost the same lockout budget as guesses at a login.
                if (!byRecoveryCode) return await RefuseAsync(users, user);
            }

            if (!await mfa.DisableAsync(user, ct))
                return Results.Json(
                    new AuthErrorResponse("Two-factor authentication could not be turned off. Please try again."),
                    statusCode: StatusCodes.Status409Conflict);

            if (await users.GetAccessFailedCountAsync(user) > 0)
                await users.ResetAccessFailedCountAsync(user);

            await AuthAudit.QueueAsync(
                db, time, AuthEventKinds.MfaTotpDisabled, user.Id, null,
                Describe(http, byRecoveryCode ? "authorised by recovery code" : null));
            await db.SaveChangesAsync(CancellationToken.None);

            return Results.Ok(new MfaStatusResponse(TotpEnabled: false, RecoveryCodesRemaining: 0));
        })
        .RequireRateLimiting(LoginRateLimiting.PolicyName);

        // ── Reissue recovery codes ──────────────────────────────────────────
        // An authenticator code only — no recovery code. Spending one recovery code to mint ten fresh ones
        // would turn a single leaked code into permanent access; proving current possession of the
        // authenticator is the whole point of the check.
        app.MapPost("/api/auth/mfa/recovery/regenerate", async (
            HttpContext http,
            UserManager<User> users,
            AuthSessionService sessions,
            UserMfaService mfa,
            WatchtowerDbContext db,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            var request = await ReadJsonAsync<MfaCodeRequest>(http, ct);
            if (request.Failure is not null) return request.Failure;

            var user = await CurrentAccountAsync(http, sessions, realmContext, ct);
            if (user is null) return Unauthenticated();

            if (!user.TwoFactorEnabled)
                return Results.Json(NotEnrolled, statusCode: StatusCodes.Status409Conflict);

            if (await users.IsLockedOutAsync(user))
                return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);

            if (!await mfa.VerifyTotpAsync(user, request.Body?.Code, ct))
                return await RefuseAsync(users, user);

            var codes = await mfa.RegenerateRecoveryCodesAsync(user, ct);
            if (codes is null)
                return Results.StatusCode(StatusCodes.Status500InternalServerError);

            if (await users.GetAccessFailedCountAsync(user) > 0)
                await users.ResetAccessFailedCountAsync(user);

            await AuthAudit.QueueAsync(
                db, time, AuthEventKinds.MfaRecoveryGenerated, user.Id, null,
                Describe(http, $"count={codes.Count}; replaced"));
            await db.SaveChangesAsync(CancellationToken.None);

            return Results.Ok(new MfaRecoveryCodesResponse(codes));
        })
        .RequireRateLimiting(LoginRateLimiting.PolicyName);
    }

    /// <summary>
    /// Counts a refused code or password against the account lockout and answers with the one generic
    /// rejection this surface has.
    /// </summary>
    /// <remarks>
    /// The same five-attempt budget the login endpoints drive, on purpose: an attacker who has a session
    /// but not the password, or the password but not the authenticator, must not get an unlimited private
    /// gallery to guess in just because they got past the front door once.
    /// </remarks>
    private static async Task<IResult> RefuseAsync(UserManager<User> users, User user) {
        await users.AccessFailedAsync(user);
        return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);
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

    /// <summary>A parsed request body, or the response that should be returned instead of reading one.</summary>
    private readonly record struct ParsedBody<T>(T? Body, IResult? Failure) where T : class;

    /// <summary>
    /// Reads a JSON body, enforcing the content type first.
    /// </summary>
    /// <remarks>
    /// The content-type requirement is the CSRF control the whole file relies on (design.md §9): a
    /// cross-site HTML form can only send "simple" content types, so refusing everything else is what keeps
    /// a forged POST from switching somebody's second factor off behind their back.
    /// </remarks>
    private static async Task<ParsedBody<T>> ReadJsonAsync<T>(HttpContext http, CancellationToken ct)
        where T : class {
        if (!http.Request.HasJsonContentType())
            return new ParsedBody<T>(null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        try {
            return new ParsedBody<T>(await http.Request.ReadFromJsonAsync<T>(ct), null);
        } catch (System.Text.Json.JsonException) {
            return new ParsedBody<T>(null, Results.BadRequest());
        }
    }

    /// <summary>
    /// Records a rejected second factor and answers with the one indistinguishable refusal. The audit write
    /// ignores <see cref="HttpContext.RequestAborted"/> for the same reason the password step's does: a
    /// caller that hangs up mid-attempt must not be able to keep its failures out of the trail.
    /// </summary>
    private static async Task<IResult> RejectSecondFactorAsync(
        WatchtowerDbContext db, TimeProvider time, int? userId, string reason, HttpContext http) {
        await AuthAudit.QueueAsync(db, time, AuthEventKinds.LoginMfaFailed, userId, null, Describe(http, reason), success: false);
        await db.SaveChangesAsync(CancellationToken.None);
        return Results.Json(InvalidSecondFactor, statusCode: StatusCodes.Status401Unauthorized);
    }
}
