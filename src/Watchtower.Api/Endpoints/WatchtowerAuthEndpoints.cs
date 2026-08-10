using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Watchtower.Api.Authentication;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// Watchtower's native login surface (docs/central-auth/design.md §2.5, §7): the SPA posts credentials to
/// <c>/api/auth/login</c> and gets the <c>__wt_sso</c> cookie; <c>/api/auth/logout</c> signs the account out
/// everywhere. Plain HTTP endpoints rather than JSON-RPC handlers, matching the existing convention for
/// externally-shaped surfaces (webhook, SSE) — and because the login page must work before any handler would
/// let the caller in.
/// </summary>
/// <remarks>
/// The same two endpoints also drive the cross-domain dance (design.md §5). A login page reached from a
/// protected app carries that app's URL, and the response then contains a <c>continueUrl</c> the browser
/// follows to the app's own callback; <c>/api/auth/continue</c> is the same step for a visitor who is
/// already signed in centrally, which is what makes the second and third app silent.
/// </remarks>
public static class WatchtowerAuthEndpoints {
    /// <summary>
    /// Credentials posted by the login form. <paramref name="RedirectUri"/> is present only when the login
    /// page was reached from a protected app, and is the URL the visitor was originally going to.
    /// </summary>
    public sealed record LoginRequest(string? UserName, string? Password, string? RedirectUri);

    /// <summary>
    /// What the SPA learns about the account it just signed in as. <paramref name="ContinueUrl"/> is set
    /// only for the cross-domain dance and is an opaque target to navigate to — the SPA never renders it.
    /// </summary>
    public sealed record LoginResponse(string UserName, bool IsAdmin, string? ContinueUrl = null);

    /// <summary>The app the caller wants to be handed over to.</summary>
    public sealed record ContinueRequest(string? RedirectUri);

    /// <summary>Where to navigate to hand the central session over to that app.</summary>
    public sealed record ContinueResponse(string ContinueUrl);

    /// <summary>The single failure body every rejected login gets, whatever the actual reason was.</summary>
    public sealed record AuthErrorResponse(string Message);

    /// <summary>
    /// Audit kinds written by this file. The vocabulary itself lives in
    /// <see cref="AuthEventKinds"/>, shared with the Users module, which writes the other half of the trail.
    /// </summary>
    private const string LoginOk = AuthEventKinds.LoginOk;
    private const string LoginFailed = AuthEventKinds.LoginFailed;
    private const string Logout = AuthEventKinds.Logout;
    private const string AccessDenied = AuthEventKinds.AccessDenied;

    /// <summary>
    /// One message for a bad password, an unknown name, a disabled account and a locked-out one alike:
    /// distinguishing them would turn the endpoint into an account-existence oracle.
    /// </summary>
    private static readonly AuthErrorResponse InvalidCredentials =
        new("Invalid user name or password.");

    /// <summary>
    /// One message for "no such app", "that app is public" and "you hold no grant for it" alike. The
    /// caller has authenticated by this point, so the message may be specific about the outcome — but not
    /// about the reason, which would otherwise enumerate which domains Watchtower proxies and who may
    /// enter them.
    /// </summary>
    private static readonly AuthErrorResponse AccessNotPermitted =
        new("You are not permitted to access that application.");

    /// <summary>
    /// Maps the login endpoints. When <paramref name="authEnabled"/> is false the routes still exist but
    /// answer 404: with no user database in play a login form is a dead end, and saying so plainly beats the
    /// 405 the SPA fallback route would otherwise produce for a POST.
    /// </summary>
    public static WebApplication MapWatchtowerAuthEndpoints(this WebApplication app, bool authEnabled) {
        if (!authEnabled) {
            app.MapPost("/api/auth/login", () => Results.NotFound());
            app.MapPost("/api/auth/logout", () => Results.NotFound());
            app.MapPost("/api/auth/continue", () => Results.NotFound());
            return app;
        }

        MapLogin(app);
        MapLogout(app);
        MapContinue(app);
        return app;
    }

    /// <summary>
    /// Password login. Every failure path returns the same body and costs one password-hash computation, so
    /// neither the response nor — to a first approximation — its timing reveals whether the account exists.
    /// </summary>
    /// <remarks>
    /// The hash parity is not perfect and is not claimed to be: a wrong password additionally writes the
    /// lockout counter (<c>AccessFailedAsync</c>), so it costs one extra round trip that the unknown-user
    /// path does not. That delta distinguishes "wrong password" from "no such account" to an attacker who
    /// can measure it precisely enough. Closing it means writing on the unknown-user path too, i.e. letting
    /// an attacker create database traffic from a made-up name — a worse trade. The hash burn removes the
    /// order-of-magnitude difference, which is the one that is trivially observable over a network.
    /// </remarks>
    private static void MapLogin(WebApplication app) {
        app.MapPost("/api/auth/login", async (
            HttpContext http,
            UserManager<User> users,
            IPasswordHasher<User> hasher,
            AuthSessionService sessions,
            IOptionsMonitor<WatchtowerOptions> options,
            WatchtowerDbContext db,
            RealmResolver realms,
            IRealmContext realmContext,
            TimeProvider time,
            CancellationToken ct) => {

            // A browser form can only ever send a "simple" content type, so requiring JSON is what keeps a
            // cross-site POST from logging someone into an attacker's account (design.md §9, CSRF).
            if (!http.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            LoginRequest? body;
            try {
                body = await http.Request.ReadFromJsonAsync<LoginRequest>(ct);
            } catch (System.Text.Json.JsonException) {
                return Results.BadRequest();
            }

            var userName = body?.UserName?.Trim();
            var password = body?.Password;
            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
                return await RejectAsync(db, time, null, "missing credentials", http);

            // Which population this login page belongs to, decided by the host it was served on — the
            // host is the realm's cookie jar, so it is also its credential space (design.md §13). Pinned
            // before anything touches UserManager, because that is what makes the name lookup below see
            // this realm's accounts and only this realm's.
            var realm = await realms.ResolveByHostAsync(http.Request.Host.Host, ct);
            realmContext.SetRealm(realm.Id);

            var user = await users.FindByNameAsync(userName);
            if (user is null) {
                // Burn one hash anyway: an unknown name must not answer measurably faster than a known one.
                _ = hasher.HashPassword(new User {
                    UserName = userName, NormalizedUserName = userName, PasswordHash = string.Empty,
                    SecurityStamp = string.Empty, ConcurrencyStamp = string.Empty,
                }, password);
                return await RejectAsync(db, time, null, $"unknown user '{userName}'", http);
            }

            // Verify before deciding, so every known-account path costs exactly one hash: checking
            // disabled/locked-out first would answer measurably faster than a wrong password and turn the
            // response time into the account-existence oracle the generic message exists to prevent.
            var passwordOk = await users.CheckPasswordAsync(user, password);

            // Refuse without counting: an attacker who can keep failing must not be able to extend someone
            // else's lockout indefinitely.
            if (await users.IsLockedOutAsync(user))
                return await RejectAsync(db, time, user.Id, "account locked out", http);

            if (!passwordOk) {
                // UserManager.CheckPasswordAsync does not count failures (that is SignInManager's job, and
                // SignInManager brings the cookie stack we are deliberately not using), so drive the lockout
                // counter here — otherwise the configured 5-attempts policy would never trigger.
                await users.AccessFailedAsync(user);
                return await RejectAsync(db, time, user.Id, "wrong password", http);
            }

            if (user.Disabled)
                return await RejectAsync(db, time, user.Id, "account disabled", http);

            if (await users.GetAccessFailedCountAsync(user) > 0)
                await users.ResetAccessFailedCountAsync(user);

            var token = await sessions.CreateSsoSessionAsync(user, ct);
            AuthCookies.Append(
                http, AuthSessionService.SsoCookieName, token,
                sessions.AbsoluteLifetime, options.CurrentValue.Auth.CookieSecure);

            Record(db, time, LoginOk, user.Id, Describe(http, reason: null));
            // Same reasoning as the failure path: the session now exists, so the row recording that it was
            // handed out must not depend on the client staying connected.
            await db.SaveChangesAsync(CancellationToken.None);

            // The credentials were good and the central session exists either way; what is still open is
            // whether this account may enter the app the visitor came from.
            if (string.IsNullOrWhiteSpace(body?.RedirectUri))
                return Results.Ok(new LoginResponse(user.UserName, user.IsAdmin));

            var continueUrl = await IssueContinueUrlAsync(db, sessions, time, http, user.Id, body.RedirectUri, ct);
            return continueUrl is null
                ? Results.Json(AccessNotPermitted, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(new LoginResponse(user.UserName, user.IsAdmin, continueUrl));
        })
        // The coarse per-IP backstop on top of the per-account lockout (design.md §9). Attached only to
        // the real login route; the 404 stubs mapped when auth is off carry no limiter.
        .RequireRateLimiting(LoginRateLimiting.PolicyName);
    }

    /// <summary>
    /// The silent-SSO step: a visitor who already holds a central session asks to be handed over to an app,
    /// without a password prompt. Same validation and the same generic refusal as the login path.
    /// </summary>
    /// <remarks>
    /// JSON content type required for the same reason login requires it: a cross-site HTML form cannot
    /// produce one, so a forged POST cannot mint a code with somebody else's session (design.md §9, CSRF).
    /// </remarks>
    private static void MapContinue(WebApplication app) {
        app.MapPost("/api/auth/continue", async (
            HttpContext http,
            AuthSessionService sessions,
            WatchtowerDbContext db,
            TimeProvider time,
            CancellationToken ct) => {

            if (!http.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            ContinueRequest? body;
            try {
                body = await http.Request.ReadFromJsonAsync<ContinueRequest>(ct);
            } catch (System.Text.Json.JsonException) {
                return Results.BadRequest();
            }

            var session = await sessions.ValidateAsync(
                http.Request.Cookies[AuthSessionService.SsoCookieName], ct);
            if (session is null) return Results.Unauthorized();

            var continueUrl = await IssueContinueUrlAsync(
                db, sessions, time, http, session.UserId, body?.RedirectUri, ct);
            return continueUrl is null
                ? Results.Json(AccessNotPermitted, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(new ContinueResponse(continueUrl));
        });
    }

    /// <summary>
    /// Validates the requested app URL, checks the account may enter it, and mints the one-time code —
    /// returning the URL to navigate to, or <see langword="null"/> when any of that fails.
    /// </summary>
    /// <remarks>
    /// Failing as one value rather than distinguishing the causes is deliberate: an unknown domain, a
    /// public route and a missing grant must be indistinguishable to the caller, or the endpoint becomes a
    /// way to enumerate the route table and its policy from an ordinary account.
    /// <para>
    /// The returned URL is built from <see cref="Route.Domain"/> — a row Watchtower wrote — not from the
    /// caller's string, so the browser is only ever sent to a domain this instance actually proxies.
    /// </para>
    /// </remarks>
    private static async Task<string?> IssueContinueUrlAsync(
        WatchtowerDbContext db,
        AuthSessionService sessions,
        TimeProvider time,
        HttpContext http,
        int userId,
        string? redirectUri,
        CancellationToken ct) {

        var target = await RouteAccessPolicy.ResolveRedirectTargetAsync(db, redirectUri, ct);
        if (target is null || !await RouteAccessPolicy.IsAuthorizedAsync(db, target.Value.Route, userId, ct)) {
            Record(db, time, AccessDenied, userId, Describe(http, "redirect_uri refused"), target?.Route.Id);
            await db.SaveChangesAsync(CancellationToken.None);
            return null;
        }

        var (route, url) = target.Value;
        var code = await sessions.MintLoginCodeAsync(userId, route.Id, url, ct);
        return $"https://{route.Domain}{RouteAccessPolicy.CallbackPath}?code={Uri.EscapeDataString(code)}";
    }

    /// <summary>Global sign-out: every session of the account is deleted, not just this browser's.</summary>
    private static void MapLogout(WebApplication app) {
        app.MapPost("/api/auth/logout", async (
            HttpContext http,
            AuthSessionService sessions,
            IOptionsMonitor<WatchtowerOptions> options,
            WatchtowerDbContext db,
            TimeProvider time,
            CancellationToken ct) => {

            var securePolicy = options.CurrentValue.Auth.CookieSecure;
            var session = await sessions.ValidateAsync(
                http.Request.Cookies[AuthSessionService.SsoCookieName], ct);
            if (session is null) {
                // Nothing to revoke, but clear whatever stale value the browser is still presenting.
                AuthCookies.Delete(http, AuthSessionService.SsoCookieName, securePolicy);
                return Results.Unauthorized();
            }

            await sessions.RevokeAllForUserAsync(session.UserId, CancellationToken.None);
            Record(db, time, Logout, session.UserId, Describe(http, reason: null));
            // The sessions are already gone; the row saying who ended them must not be lost to a disconnect.
            await db.SaveChangesAsync(CancellationToken.None);

            AuthCookies.Delete(http, AuthSessionService.SsoCookieName, securePolicy);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Records the failure and answers with the one indistinguishable rejection. The audit write
    /// deliberately ignores <see cref="HttpContext.RequestAborted"/>: a caller that hangs up mid-attempt
    /// must not be able to keep its failed logins out of the trail.
    /// </summary>
    private static async Task<IResult> RejectAsync(
        WatchtowerDbContext db, TimeProvider time, int? userId, string reason, HttpContext http) {
        Record(db, time, LoginFailed, userId, Describe(http, reason));
        await db.SaveChangesAsync(CancellationToken.None);
        return Results.Json(InvalidCredentials, statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>Queues an audit row; the caller decides when to commit it alongside its other writes.</summary>
    private static void Record(
        WatchtowerDbContext db, TimeProvider time, string kind, int? userId, string? detail, int? routeId = null) =>
        db.AuthEvents.Add(new AuthEvent {
            Kind = kind,
            UserId = userId,
            RouteId = routeId,
            Detail = detail,
            CreatedAt = time.GetUtcNow(),
        });

    /// <summary>Audit detail: the reason (when there is one) plus the remote address, never the credentials.</summary>
    private static string Describe(HttpContext http, string? reason) {
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return reason is null ? $"from {address}" : $"{reason}; from {address}";
    }

}
