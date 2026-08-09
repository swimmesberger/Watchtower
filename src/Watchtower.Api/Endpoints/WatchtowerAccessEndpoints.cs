using System.Text;
using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Api.Authentication;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

// Watchtower's own entity, not Microsoft.AspNetCore.Routing.Route — which ImplicitUsings pulls in here.
using Route = Watchtower.Application.Entities.Route;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The forward-auth surface (docs/central-auth/design.md §5, §7): the verify endpoint Caddy consults for
/// every request to a protected app, the code-redemption callback and per-app sign-out served on each
/// app's own domain, and the public JWKS.
/// </summary>
/// <remarks>
/// All four are anonymous, and have to be: verify <em>is</em> the authentication check, the callback is
/// how a visitor becomes authenticated on that domain in the first place, and the JWKS is public key
/// material. None of them trusts <c>X-Forwarded-Proto</c> — scheme is not load-bearing anywhere here, and
/// the one URL built from configuration (the login redirect) hard-codes <c>https</c>.
/// </remarks>
public static class WatchtowerAccessEndpoints {
    /// <summary>Audit kind written when a signed-in visitor is refused an app they hold no grant for.</summary>
    private const string AccessDenied = "access.denied";

    /// <summary>Audit kind written when a login code is exchanged for an app session.</summary>
    private const string CodeRedeemed = "code.redeemed";

    /// <summary>
    /// Caps the length of the <c>redirect_uri</c> echoed into the login redirect. A caller controls
    /// <c>X-Forwarded-Uri</c>, and an unbounded value would become an unbounded <c>Location</c> header.
    /// </summary>
    private const int MaxOriginalUriLength = 2000;

    /// <summary>
    /// Set once the missing-<c>Auth:Host</c> warning has been logged. Verify runs on every proxied request,
    /// so a misconfiguration that cannot be fixed from here must not also flood the log.
    /// </summary>
    private static int _authHostWarningLogged;

    /// <summary>
    /// Maps the forward-auth endpoints. With <paramref name="authEnabled"/> false they answer 404 rather
    /// than being left unmapped: <c>/.watchtower/*</c> would otherwise fall through to the SPA fallback and
    /// return <c>index.html</c> with a 200, which is a confusing answer to a callback.
    /// </summary>
    public static WebApplication MapWatchtowerAccessEndpoints(this WebApplication app, bool authEnabled) {
        if (!authEnabled) {
            app.MapGet(RouteAccessPolicy.VerifyPath, () => Results.NotFound());
            app.MapGet("/api/auth/jwks", () => Results.NotFound());
            app.MapGet(RouteAccessPolicy.CallbackPath, () => Results.NotFound());
            app.MapGet(RouteAccessPolicy.AppLogoutPath, () => Results.NotFound());
            return app;
        }

        MapVerify(app);
        MapJwks(app);
        MapCallback(app);
        MapAppLogout(app);
        return app;
    }

    // ── Verify ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>forward_auth</c> target. Caddy proxies the original request's headers here (including
    /// <c>Cookie</c>) plus <c>X-Forwarded-Method</c>/<c>-Uri</c>/<c>-Host</c>, and treats the status as the
    /// verdict: 2xx lets the request through with <c>copy_headers</c> applied, anything else is returned to
    /// the client verbatim.
    /// </summary>
    private static void MapVerify(WebApplication app) {
        app.MapGet(RouteAccessPolicy.VerifyPath, async (
            HttpContext http,
            WatchtowerDbContext db,
            AuthSessionService sessions,
            AuthTokenSigner signer,
            IOptionsMonitor<WatchtowerOptions> options,
            TimeProvider time,
            ILoggerFactory loggerFactory,
            CancellationToken ct) => {

            // 1. Which app is this? The host names the route; an unknown one is not a Watchtower app.
            var host = RouteAccessPolicy.NormalizeForwardedHost(http.Request.Headers["X-Forwarded-Host"]);
            if (host is null) return Results.NotFound();

            var route = await RouteAccessPolicy.FindRouteByHostAsync(db, host, ct);
            if (route is null) return Results.NotFound();

            // No forward_auth is emitted for a public route, so reaching here means the config is stale.
            // Letting the request through matches what the proxy would have done without us.
            if (route.AccessMode == AccessMode.Public) return Results.Ok();

            // 2. Exempt paths answer before any identity is considered, and carry no identity headers —
            //    a bypass path is "no access control here", not "anonymous access as somebody".
            var forwardedUri = http.Request.Headers["X-Forwarded-Uri"].ToString();
            var path = RouteAccessPolicy.ExtractPath(forwardedUri);
            if (RouteAccessPolicy.IsExemptPath(route.BypassPaths, path)) return Results.Ok();

            // 3. The per-app session. Deliberately not ct: validation may renew the sliding window, and a
            //    client that hangs up must not turn that write into a cancellation out of the auth check.
            var session = await sessions.ValidateAppSessionAsync(
                http.Request.Cookies[AuthSessionService.AccessCookieName], route.Id, CancellationToken.None);

            if (session?.User is null)
                return ChallengeAnonymous(http, route, forwardedUri, options.CurrentValue.Auth, loggerFactory);

            // 4. Signed in, but policy may still refuse this app.
            if (!await RouteAccessPolicy.IsAuthorizedAsync(db, route, session.UserId, ct))
                return await DenyAsync(db, time, route, session.UserId, http);

            WriteIdentityHeaders(http, session.User, route, signer);
            return Results.Ok();
        });
    }

    /// <summary>
    /// What an unauthenticated request gets: a browser navigation is sent to the central login page, and
    /// everything else gets a plain 401 — redirecting an XHR or a POST into a login form would turn a
    /// clean failure into a mystery, and would replay the body nowhere useful.
    /// </summary>
    /// <remarks>
    /// The redirect is assembled from <em>configuration</em> and the route row: literal <c>https</c>, the
    /// configured auth host, and the route's own domain. <c>X-Forwarded-Proto</c> and
    /// <c>X-Forwarded-Host</c> never reach the target — the only caller-supplied part is the path, which is
    /// bounded, required to be rooted, and percent-encoded into a query parameter.
    /// </remarks>
    private static IResult ChallengeAnonymous(
        HttpContext http, Route route, string? forwardedUri, AuthOptions auth, ILoggerFactory loggerFactory) {

        var authHost = RouteAccessPolicy.NormalizeForwardedHost(auth.Host);
        if (authHost is null) {
            WarnMissingAuthHostOnce(loggerFactory);
            return Results.Unauthorized();
        }

        if (!IsBrowserNavigation(http)) return Results.Unauthorized();

        var original = $"https://{route.Domain}{OriginalPathAndQuery(forwardedUri)}";
        return Results.Redirect($"https://{authHost}/login?redirect_uri={Uri.EscapeDataString(original)}");
    }

    /// <summary>
    /// A request the visitor would follow with their eyes: a document fetch. Caddy sends verify itself as a
    /// GET and carries the original method in <c>X-Forwarded-Method</c>, so that header is the authority
    /// when present.
    /// </summary>
    private static bool IsBrowserNavigation(HttpContext http) {
        var method = http.Request.Headers["X-Forwarded-Method"].ToString();
        if (string.IsNullOrEmpty(method)) method = http.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)) return false;
        return http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The caller-supplied part of the original URL, or <c>/</c> when it is missing or unusable.</summary>
    private static string OriginalPathAndQuery(string? forwardedUri) =>
        string.IsNullOrEmpty(forwardedUri) || forwardedUri[0] != '/' || forwardedUri.Length > MaxOriginalUriLength
            ? "/"
            : forwardedUri;

    /// <summary>
    /// Authenticated but not authorised. Recorded, then answered with the denial page rather than a
    /// redirect: sending them back to a login they have already completed would loop.
    /// </summary>
    private static async Task<IResult> DenyAsync(
        WatchtowerDbContext db, TimeProvider time, Route route, int userId, HttpContext http) {
        db.AuthEvents.Add(new AuthEvent {
            Kind = AccessDenied,
            UserId = userId,
            RouteId = route.Id,
            Detail = Describe(http),
            CreatedAt = time.GetUtcNow(),
        });
        // Not RequestAborted: a caller that disconnects must not be able to keep denials out of the trail.
        await db.SaveChangesAsync(CancellationToken.None);

        return Html(StatusCodes.Status403Forbidden, "Access denied",
            $"You are signed in, but your account is not permitted to use {Encode(route.Domain)}.",
            "Ask an administrator to grant you access.");
    }

    /// <summary>
    /// Sets the convenience headers and the assertion. Caddy strips these names from the inbound request
    /// before calling us, so what the upstream receives is only ever what is written here.
    /// </summary>
    private static void WriteIdentityHeaders(HttpContext http, User user, Route route, AuthTokenSigner signer) {
        var name = HeaderSafe(user.UserName);
        if (name is not null) http.Response.Headers[RouteAccessPolicy.UserHeaderName] = name;

        var email = HeaderSafe(user.Email);
        if (email is not null) http.Response.Headers[RouteAccessPolicy.EmailHeaderName] = email;

        http.Response.Headers[RouteAccessPolicy.JwtHeaderName] = signer.Mint(user, route.Domain);
    }

    /// <summary>
    /// The value if it is safe to put in a header, otherwise <see langword="null"/>. Administrators choose
    /// user names and emails, so this is not an untrusted input — but a header value is a place where a
    /// stray control character stops being a display problem, and the JWT carries the same fields anyway,
    /// so dropping the convenience copy loses nothing an app cannot recover.
    /// </summary>
    private static string? HeaderSafe(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var c in value)
            if (c is < ' ' or > '~') return null;
        return value;
    }

    // ── JWKS ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Public key material for verifying <c>X-Watchtower-Jwt</c>. Cacheable: the document changes only
    /// when the key file does, which needs a restart.
    /// </summary>
    private static void MapJwks(WebApplication app) {
        app.MapGet("/api/auth/jwks", (HttpContext http, AuthTokenSigner signer) => {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Text(signer.JwksDocument, "application/jwk-set+json", Encoding.UTF8);
        });
    }

    // ── Callback + per-app logout (served on the app's own domain) ─────────────

    /// <summary>
    /// Redeems the one-time code minted by the central login and turns it into this domain's
    /// <c>__wt_access</c> cookie, then returns the visitor to where they were going.
    /// </summary>
    /// <remarks>
    /// Host-agnostic by necessity — it executes on every protected app's domain — so the binding comes from
    /// the code itself: it names the route, and when Caddy tells us which domain this ran on the two must
    /// agree. Authorisation is re-checked here rather than trusted from mint time, because a grant may have
    /// been revoked in the seconds between.
    /// </remarks>
    private static void MapCallback(WebApplication app) {
        app.MapGet(RouteAccessPolicy.CallbackPath, async (
            HttpContext http,
            WatchtowerDbContext db,
            AuthSessionService sessions,
            IOptionsMonitor<WatchtowerOptions> options,
            TimeProvider time,
            CancellationToken ct) => {

            // Redemption deletes the row; a disconnect must not leave a code that looks unused.
            var grant = await sessions.RedeemLoginCodeAsync(http.Request.Query["code"], CancellationToken.None);
            if (grant is null) return ExpiredCodePage();

            var route = await db.Routes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == grant.RouteId, ct);
            if (route is null) return ExpiredCodePage();

            var host = RouteAccessPolicy.NormalizeForwardedHost(http.Request.Headers["X-Forwarded-Host"]);
            if (host is not null && !string.Equals(host, route.Domain, StringComparison.OrdinalIgnoreCase))
                return ExpiredCodePage();

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == grant.UserId, ct);
            if (user is null || user.Disabled) return ExpiredCodePage();

            if (route.AccessMode == AccessMode.Public ||
                !await RouteAccessPolicy.IsAuthorizedAsync(db, route, user.Id, ct)) {
                return Html(StatusCodes.Status403Forbidden, "Access denied",
                    $"Your account is not permitted to use {Encode(route.Domain)}.",
                    "Ask an administrator to grant you access.");
            }

            var token = await sessions.CreateAppSessionAsync(user, route.Id, CancellationToken.None);
            AuthCookies.Append(
                http, AuthSessionService.AccessCookieName, token,
                sessions.AbsoluteLifetime, options.CurrentValue.Auth.CookieSecure);

            db.AuthEvents.Add(new AuthEvent {
                Kind = CodeRedeemed,
                UserId = user.Id,
                RouteId = route.Id,
                Detail = Describe(http),
                CreatedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(CancellationToken.None);

            // Re-parse rather than trusting the stored string: the redirect target is re-derived from a
            // validated URL whose host must still be this route's, so nothing reaches Location unchecked.
            var target = RouteAccessPolicy.ParseAppRedirectUri(grant.RedirectUri);
            var location = target is not null &&
                           string.Equals(target.IdnHost, route.Domain, StringComparison.OrdinalIgnoreCase)
                ? target.AbsoluteUri
                : $"https://{route.Domain}/";
            return Results.Redirect(location);
        });
    }

    /// <summary>
    /// Per-app sign-out: drops this domain's session row and cookie and nothing else. The central session
    /// and the visitor's other apps are untouched — global sign-out is <c>/api/auth/logout</c> on the auth host.
    /// </summary>
    private static void MapAppLogout(WebApplication app) {
        app.MapGet(RouteAccessPolicy.AppLogoutPath, async (
            HttpContext http,
            AuthSessionService sessions,
            IOptionsMonitor<WatchtowerOptions> options) => {

            await sessions.RevokeAppSessionAsync(
                http.Request.Cookies[AuthSessionService.AccessCookieName], CancellationToken.None);
            AuthCookies.Delete(http, AuthSessionService.AccessCookieName, options.CurrentValue.Auth.CookieSecure);
            return Results.Redirect("/");
        });
    }

    // ── Shared bits ───────────────────────────────────────────────────────────

    /// <summary>
    /// The answer to a code that is unknown, already used or older than a minute. Deliberately one page for
    /// all three, and deliberately free of any link built from the request: the visitor's own app URL is
    /// the way back, and it is already in their address bar.
    /// </summary>
    private static IResult ExpiredCodePage() =>
        Html(StatusCodes.Status401Unauthorized, "Sign-in link expired",
            "This sign-in link has already been used or has expired.",
            "Reload the application's address to sign in again.");

    /// <summary>
    /// A minimal self-contained page: no stylesheet, no script, and nothing taken from the request.
    /// </summary>
    /// <param name="messageHtml">
    /// The one interpolated fragment, and therefore the caller's responsibility: any value that is not a
    /// literal must already have been through <see cref="Encode"/>. <paramref name="title"/> and
    /// <paramref name="hint"/> are encoded here.
    /// </param>
    private static IResult Html(int statusCode, string title, string messageHtml, string hint) {
        var encodedTitle = Encode(title);
        var body = $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{encodedTitle}</title>
            </head>
            <body style="font:16px/1.5 system-ui,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh">
            <main style="max-width:32rem;padding:2rem;text-align:center">
            <h1 style="font-size:1.25rem;margin:0 0 .5rem">{encodedTitle}</h1>
            <p style="margin:0 0 .5rem">{messageHtml}</p>
            <p style="margin:0;opacity:.7">{Encode(hint)}</p>
            </main>
            </body>
            </html>
            """;
        return Results.Content(body, "text/html", Encoding.UTF8, statusCode);
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    /// <summary>Audit detail: the remote address, never a cookie or a code.</summary>
    private static string Describe(HttpContext http) =>
        $"from {http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    /// <summary>
    /// Says once that protected routes cannot redirect anywhere because no auth host is configured. Until
    /// it is, every anonymous request to a protected app gets a bare 401 instead of the login page.
    /// </summary>
    private static void WarnMissingAuthHostOnce(ILoggerFactory loggerFactory) {
        if (Interlocked.Exchange(ref _authHostWarningLogged, 1) != 0) return;
        loggerFactory
            .CreateLogger(typeof(WatchtowerAccessEndpoints).FullName!)
            .LogWarning(
                "Auth:Host is not configured, so unauthenticated requests to protected apps are answered " +
                "with 401 instead of being redirected to the login page. Set Watchtower:Auth:Host to the " +
                "hostname the Watchtower UI is reachable on.");
    }
}
