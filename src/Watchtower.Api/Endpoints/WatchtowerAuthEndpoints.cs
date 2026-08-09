using Microsoft.AspNetCore.Identity;
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
public static class WatchtowerAuthEndpoints {
    /// <summary>Credentials posted by the login form.</summary>
    public sealed record LoginRequest(string? UserName, string? Password);

    /// <summary>What the SPA learns about the account it just signed in as.</summary>
    public sealed record LoginResponse(string UserName, bool IsAdmin);

    /// <summary>The single failure body every rejected login gets, whatever the actual reason was.</summary>
    public sealed record AuthErrorResponse(string Message);

    /// <summary>Audit kinds written by this file (design.md §9; the full audit surface is a later work item).</summary>
    private const string LoginOk = "login.ok";
    private const string LoginFailed = "login.failed";
    private const string Logout = "logout";

    /// <summary>
    /// One message for a bad password, an unknown name, a disabled account and a locked-out one alike:
    /// distinguishing them would turn the endpoint into an account-existence oracle.
    /// </summary>
    private static readonly AuthErrorResponse InvalidCredentials =
        new("Invalid user name or password.");

    /// <summary>
    /// Maps the login endpoints. When <paramref name="authEnabled"/> is false the routes still exist but
    /// answer 404: with no user database in play a login form is a dead end, and saying so plainly beats the
    /// 405 the SPA fallback route would otherwise produce for a POST.
    /// </summary>
    public static WebApplication MapWatchtowerAuthEndpoints(this WebApplication app, bool authEnabled) {
        if (!authEnabled) {
            app.MapPost("/api/auth/login", () => Results.NotFound());
            app.MapPost("/api/auth/logout", () => Results.NotFound());
            return app;
        }

        MapLogin(app);
        MapLogout(app);
        return app;
    }

    /// <summary>
    /// Password login. Every failure path costs one password-hash computation and returns the same body, so
    /// neither the response nor its timing reveals whether the account exists.
    /// </summary>
    private static void MapLogin(WebApplication app) {
        app.MapPost("/api/auth/login", async (
            HttpContext http,
            UserManager<User> users,
            IPasswordHasher<User> hasher,
            AuthSessionService sessions,
            WatchtowerDbContext db,
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
            AppendSessionCookie(http, token, sessions.AbsoluteLifetime);

            Record(db, time, LoginOk, user.Id, Describe(http, reason: null));
            await db.SaveChangesAsync(ct);

            return Results.Ok(new LoginResponse(user.UserName, user.IsAdmin));
        });
    }

    /// <summary>Global sign-out: every session of the account is deleted, not just this browser's.</summary>
    private static void MapLogout(WebApplication app) {
        app.MapPost("/api/auth/logout", async (
            HttpContext http,
            AuthSessionService sessions,
            WatchtowerDbContext db,
            TimeProvider time,
            CancellationToken ct) => {

            var session = await sessions.ValidateAsync(
                http.Request.Cookies[AuthSessionService.SsoCookieName], ct);
            if (session is null) {
                // Nothing to revoke, but clear whatever stale value the browser is still presenting.
                DeleteSessionCookie(http);
                return Results.Unauthorized();
            }

            await sessions.RevokeAllForUserAsync(session.UserId, ct);
            Record(db, time, Logout, session.UserId, Describe(http, reason: null));
            await db.SaveChangesAsync(ct);

            DeleteSessionCookie(http);
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
    private static void Record(WatchtowerDbContext db, TimeProvider time, string kind, int? userId, string? detail) =>
        db.AuthEvents.Add(new AuthEvent {
            Kind = kind,
            UserId = userId,
            RouteId = null,
            Detail = detail,
            CreatedAt = time.GetUtcNow(),
        });

    /// <summary>Audit detail: the reason (when there is one) plus the remote address, never the credentials.</summary>
    private static string Describe(HttpContext http, string? reason) {
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return reason is null ? $"from {address}" : $"{reason}; from {address}";
    }

    /// <summary>
    /// Host-scoped (no <c>Domain</c>), <c>HttpOnly</c>, <c>SameSite=Lax</c>, and <c>Secure</c> whenever the
    /// request arrived over TLS — never unconditionally, because the published-port deployment is plain HTTP
    /// and a <c>Secure</c> cookie there would simply never come back (design.md §4, §9, §11).
    /// </summary>
    private static void AppendSessionCookie(HttpContext http, string token, TimeSpan maxAge) =>
        http.Response.Cookies.Append(AuthSessionService.SsoCookieName, token, new CookieOptions {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            // The database row is the authority on expiry; MaxAge only stops the browser from keeping a
            // value that can no longer be valid, so it tracks the absolute cap rather than the idle window.
            MaxAge = maxAge,
        });

    /// <summary>Expires the cookie. The attributes must match the ones it was set with or the browser keeps it.</summary>
    private static void DeleteSessionCookie(HttpContext http) =>
        http.Response.Cookies.Delete(AuthSessionService.SsoCookieName, new CookieOptions {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
}
