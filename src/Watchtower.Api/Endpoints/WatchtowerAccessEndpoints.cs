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
/// app's own domain, and the public JWKS. Alongside them sit the two endpoints any realm's user may call
/// about themselves — UserInfo and the applications list.
/// </summary>
/// <remarks>
/// The forward-auth four are anonymous, and have to be: verify <em>is</em> the authentication check, the
/// callback is how a visitor becomes authenticated on that domain in the first place, and the JWKS is
/// public key material. None of them trusts <c>X-Forwarded-Proto</c> — scheme is not load-bearing anywhere
/// here, and the one URL built from configuration (the login redirect) hard-codes <c>https</c>.
/// <para>
/// UserInfo and <c>/api/access/apps</c> are anonymous in the ASP.NET sense for the same reason: they
/// authenticate the caller themselves, from the credential they were presented. What makes them the
/// <em>any-realm</em> surface is that neither is behind
/// <see cref="WatchtowerSessionDefaults.SystemRealmPolicy"/> — the management surface stays operator-only,
/// and these two answer a caller only about themselves (design.md §13).
/// </para>
/// </remarks>
public static class WatchtowerAccessEndpoints {
    /// <summary>One application a caller may enter: where to send them, plus what to call it.</summary>
    /// <param name="Domain">The public hostname — what the visitor sees in their own address bar.</param>
    /// <param name="Name">
    /// The stack's name (for a tenant, <c>{category}-{slug}</c>). A display label, deliberately not an id:
    /// nothing here is a handle the caller could use against another surface.
    /// </param>
    /// <param name="Url">
    /// The absolute URL to navigate to, built here rather than in the browser so the scheme follows the
    /// route's own <see cref="Route.TlsEnabled"/>. A plain-HTTP route linked as <c>https</c> would be a
    /// connection failure, and the client has nothing to derive the answer from.
    /// </param>
    public sealed record AppLinkDto(string Domain, string Name, string Url);

    /// <summary>The applications list answered by <c>/api/access/apps</c>.</summary>
    public sealed record AppsResponse(IReadOnlyList<AppLinkDto> Apps);

    /// <summary>
    /// The columns <c>/api/access/apps</c> reads off a route. A projection rather than the entity, because
    /// <see cref="Stack"/> carries <see cref="Stack.WebhookToken"/> and <see cref="Stack.AppApiToken"/> and
    /// nothing that any realm account can trigger should pull those into memory at all.
    /// </summary>
    private sealed record AppRouteRow(
        int Id,
        string Domain,
        AccessMode AccessMode,
        bool TlsEnabled,
        bool IsPrimary,
        int StackId,
        string ServiceName,
        string StackName);

    /// <summary>Audit kind written when a signed-in visitor is refused an app they hold no grant for.</summary>
    private const string AccessDenied = "access.denied";

    /// <summary>Audit kind written when a login code is exchanged for an app session.</summary>
    private const string CodeRedeemed = "code.redeemed";

    /// <summary>OIDC UserInfo (OpenID Connect Core 1.0 §5.3), served on the auth host for bearer callers.</summary>
    private const string UserInfoApiPath = "/api/access/userinfo";

    /// <summary>The same UserInfo handler on every protected app's own domain, for same-origin cookie callers.</summary>
    private const string UserInfoAppPath = "/.watchtower/userinfo";

    /// <summary>The applications the calling account may enter, served on the auth host for the SPA portal.</summary>
    private const string AppsApiPath = "/api/access/apps";

    /// <summary>
    /// Caps the length of the <c>redirect_uri</c> echoed into the login redirect. A caller controls
    /// <c>X-Forwarded-Uri</c>, and an unbounded value would become an unbounded <c>Location</c> header.
    /// </summary>
    private const int MaxOriginalUriLength = 2000;

    /// <summary>
    /// Realms whose missing-login-host warning has already been logged, by id. Verify runs on every
    /// proxied request, so a misconfiguration that cannot be fixed from here must not also flood the log —
    /// but one realm having no host says nothing about another, so the suppression is per realm rather
    /// than global.
    /// </summary>
    /// <remarks>
    /// Keyed by id rather than slug: a realm that was deleted and recreated under the same slug is a new
    /// population with a new misconfiguration, and it should say so again rather than inherit the old
    /// one's silence.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> WarnedRealms = new();

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
            app.MapGet(UserInfoApiPath, () => Results.NotFound());
            app.MapGet(UserInfoAppPath, () => Results.NotFound());
            app.MapGet(AppsApiPath, () => Results.NotFound());
            return app;
        }

        MapVerify(app);
        MapJwks(app);
        MapCallback(app);
        MapAppLogout(app);
        MapUserInfo(app);
        MapApps(app);
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
            RealmResolver realms,
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

            // The realm is loaded with the account, so an authorised request costs no extra read for it;
            // the anonymous branch is the one that has to go and ask which population owns this route.
            if (session?.User?.Realm is null)
                return await ChallengeAnonymousAsync(http, route, forwardedUri, realms, loggerFactory, ct);

            // 4. Signed in, but policy may still refuse this app — including because the account belongs to
            //    another realm, which IsAuthorizedAsync folds into the same single refusal as a missing grant.
            if (!await RouteAccessPolicy.IsAuthorizedAsync(db, route, session.UserId, ct))
                return await DenyAsync(db, time, route, session.UserId, http);

            // 5. Authorised. One membership read feeds both forwarding channels — see WriteIdentityHeaders.
            //    The account's realm is the route's realm by the check above, so it is what the assertion is
            //    minted for.
            var groups = await GroupMembership.NamesAsync(db, session.UserId, ct);
            WriteIdentityHeaders(
                http, session.User, route, signer, RealmIdentity.From(session.User.Realm), groups);
            return Results.Ok();
        });
    }

    /// <summary>
    /// What an unauthenticated request gets: a browser navigation is sent to the central login page, and
    /// everything else gets a plain 401 — redirecting an XHR or a POST into a login form would turn a
    /// clean failure into a mystery, and would replay the body nowhere useful.
    /// </summary>
    /// <remarks>
    /// The login host is <em>the route's realm's</em> (docs/central-auth/design.md §13): the configured
    /// <c>Auth:Host</c> for a system-realm route, and the realm's own <c>AuthHost</c> for any other — so a
    /// visitor is only ever sent to the login page of the population that could actually admit them. A realm
    /// created before its DNS exists has no host, and its routes then fail closed with a bare 401 rather
    /// than redirecting somewhere arbitrary, exactly as an instance with no <c>Auth:Host</c> already did.
    /// <para>
    /// The redirect is assembled from <em>stored</em> values: literal <c>https</c>, the realm's login host,
    /// and the route's own domain. <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c> never reach the
    /// target — the only caller-supplied part is the path, which is bounded, required to be rooted, and
    /// percent-encoded into a query parameter.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ChallengeAnonymousAsync(
        HttpContext http,
        Route route,
        string? forwardedUri,
        RealmResolver realms,
        ILoggerFactory loggerFactory,
        CancellationToken ct) {

        var realm = await realms.RealmForRouteAsync(route, ct);
        var authHost = realms.LoginHostFor(realm);
        if (authHost is null) {
            WarnMissingAuthHostOnce(loggerFactory, realm);
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
        await AuthAudit.QueueAsync(db, time, AccessDenied, userId, route.Id, Describe(http),
            success: false, target: route.Domain);
        // Not RequestAborted: a caller that disconnects must not be able to keep denials out of the trail.
        await db.SaveChangesAsync(CancellationToken.None);

        return Html(StatusCodes.Status403Forbidden, "Access denied",
            $"You are signed in, but your account is not permitted to use {Encode(route.Domain)}.",
            "Ask an administrator to grant you access.");
    }

    /// <summary>
    /// Sets the identity headers on a verified request. The signed assertion is <em>always</em> written — it
    /// is the source of truth (design.md §2.3). Plaintext convenience headers are written only when the route
    /// opted into a mode, under that mode's ecosystem-standard names, read from the single-source
    /// <see cref="IdentityForwarding"/> helper so the set can never drift from what Caddy strips and copies.
    /// Caddy strips every forwardable name from the inbound request before calling us, so what the upstream
    /// receives is only ever what is written here.
    /// </summary>
    /// <param name="groups">
    /// The account's group names, read once by the caller and used for both channels. Reading them twice —
    /// once for the assertion and once for the header — would let a membership change in between put an
    /// upstream in the position of seeing two different answers to the same question in one request.
    /// </param>
    /// <param name="realm">
    /// The account's realm, which is the route's by the authorisation check that precedes this: it decides
    /// the assertion's <c>iss</c> and is stated in its <c>realm</c> claim (design.md §13).
    /// </param>
    private static void WriteIdentityHeaders(
        HttpContext http,
        User user,
        Route route,
        AuthTokenSigner signer,
        RealmIdentity realm,
        IReadOnlyList<string> groups) {
        // Source of truth, forwarded for every protected route regardless of mode. It carries the groups
        // even on a None route: the signed assertion is where a group-aware app should read them from.
        var assertion = signer.Mint(user, route.Domain, realm, groups);
        http.Response.Headers[RouteAccessPolicy.JwtHeaderName] = assertion;
        // Cloudflare mode: the same assertion also travels under Cloudflare's header name, so an app
        // written against Cf-Access-Jwt-Assertion only re-points its JWKS/issuer config at Watchtower.
        if (route.IdentityHeaderMode == IdentityHeaderMode.Cloudflare)
            http.Response.Headers[IdentityForwarding.CfAccessJwtAssertion] = assertion;

        // Plaintext convenience headers: only for a route that asked for them, and only values safe to put
        // in a header (the email and group entries are already omitted by the helper when there is nothing
        // to say). Group names are constrained to printable ASCII at creation time, so the joined value the
        // helper produces survives HeaderSafe intact.
        foreach (var (headerName, value) in IdentityForwarding.PlaintextHeaders(
                     route.IdentityHeaderMode, user.UserName, user.Email, groups)) {
            var safe = HeaderSafe(value);
            if (safe is not null) http.Response.Headers[headerName] = safe;
        }
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

            // Caddy always sets X-Forwarded-Host for a request that reached the app's own site block, so
            // its absence means this did not arrive through one — refuse rather than mint a cookie scoped
            // to a host the code was not bound to. A present header must match the code's route domain.
            var host = RouteAccessPolicy.NormalizeForwardedHost(http.Request.Headers["X-Forwarded-Host"]);
            if (host is null || !string.Equals(host, route.Domain, StringComparison.OrdinalIgnoreCase))
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

            await AuthAudit.QueueAsync(db, time, CodeRedeemed, user.Id, route.Id, Describe(http), target: route.Domain);
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

    // ── UserInfo (OpenID Connect Core 1.0 §5.3) ────────────────────────────────

    /// <summary>
    /// The standards-based identity endpoint for rich or on-demand identity (design.md §5.3): the same
    /// pattern as Cloudflare Access's <c>get-identity</c>, but OIDC-shaped. One handler, two mount points —
    /// <c>/api/access/userinfo</c> on the auth host (bearer callers) and <c>/.watchtower/userinfo</c> on
    /// every protected app's own domain (same-origin cookie callers, since Caddy routes <c>/.watchtower/*</c>
    /// to Watchtower). Anonymous like the rest of this surface: it authenticates the caller itself.
    /// </summary>
    private static void MapUserInfo(WebApplication app) {
        app.MapGet(UserInfoApiPath, UserInfoAsync);
        app.MapGet(UserInfoAppPath, UserInfoAsync);
    }

    /// <summary>
    /// Answers with the caller's identity as OIDC-standard claims, or a 401 when no acceptable credential is
    /// presented. Two are accepted, tried in this order:
    /// <list type="number">
    ///   <item><description>
    ///     <c>Authorization: Bearer &lt;Watchtower JWT&gt;</c> — the standard UserInfo path, where an app
    ///     presents the assertion it received. The signature, algorithm, expiry and issuer are all checked
    ///     (<see cref="AuthTokenSigner.TryValidate(string?, out int)"/> — the overload that does not
    ///     constrain the audience, since an app may present an assertion minted for its own domain).
    ///   </description></item>
    ///   <item><description>
    ///     the <c>__wt_access</c> cookie — the browser same-origin path, resolved to its session by hash.
    ///   </description></item>
    /// </list>
    /// Either way the account is reloaded fresh and refused if it is gone or disabled, so an assertion minted
    /// minutes before the account was disabled returns no identity now.
    /// </summary>
    private static async Task<IResult> UserInfoAsync(
        HttpContext http,
        WatchtowerDbContext db,
        AuthSessionService sessions,
        AuthTokenSigner signer,
        RealmResolver realms) {
        var user = await ResolveUserInfoSubjectAsync(http, db, sessions, signer, realms);
        if (user is null) {
            // RFC 6750 / OIDC shape: a bare invalid-token challenge, no detail that could aid enumeration.
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Results.Unauthorized();
        }

        var claims = new System.Text.Json.Nodes.JsonObject {
            ["sub"] = user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["preferred_username"] = user.UserName,
        };
        if (!string.IsNullOrWhiteSpace(user.Email)) claims["email"] = user.Email;

        // Groups are always stated, empty array included: the same reasoning as the JWT claim — a caller
        // mapping groups onto its own roles has to be able to tell "in no group" from "not answered". Read
        // as of now against the freshly reloaded account, so a membership revoked a moment ago is gone here
        // even if an assertion minted minutes earlier still lists it.
        var groups = await GroupMembership.NamesAsync(db, user.Id, CancellationToken.None);
        var groupClaims = new System.Text.Json.Nodes.JsonArray();
        foreach (var group in groups) groupClaims.Add(group);
        claims["groups"] = groupClaims;

        // Real and useful; email_verified is deliberately absent (we do not verify addresses). Gated on the
        // realm by the same rule as the login principal (WatchtowerClaims.ForUser): the Admin role
        // administers the whole instance, so an account outside the operator realm must not be described as
        // holding it — an app that maps the claim onto its own roles would otherwise be told otherwise by
        // the two channels.
        if (user.IsAdmin && user.RealmId == Realm.SystemRealmId)
            claims["roles"] = new System.Text.Json.Nodes.JsonArray(WatchtowerClaims.AdminRole);

        return Results.Content(claims.ToJsonString(), "application/json", Encoding.UTF8);
    }

    /// <summary>
    /// Resolves the account behind a UserInfo request from a bearer assertion or the <c>__wt_access</c>
    /// cookie, or <see langword="null"/> when neither yields a live, enabled account. A bearer header short-
    /// circuits the cookie: a caller that presented a token but a bad one is refused, not silently fallen
    /// back on.
    /// </summary>
    /// <remarks>
    /// This is the one surface with no realm in context — an app presents whatever it was handed, on a host
    /// that says nothing about the population — so it accepts <em>any</em> realm's issuer and then checks
    /// that the resolved account is in the realm whose issuer was actually presented (design.md §13). One
    /// key pair signs every realm, so without that second step "a valid Watchtower assertion" would be
    /// accepted as "a valid assertion about this realm's user".
    /// </remarks>
    private static async Task<User?> ResolveUserInfoSubjectAsync(
        HttpContext http,
        WatchtowerDbContext db,
        AuthSessionService sessions,
        AuthTokenSigner signer,
        RealmResolver realms) {
        var bearer = ExtractBearerToken(http.Request.Headers.Authorization.ToString());
        if (bearer is not null) {
            var issuers = await realms.IssuersAsync(CancellationToken.None);
            if (!signer.TryValidate(bearer, [.. issuers.Keys], out var userId, out var issuer)) return null;
            if (!issuers.TryGetValue(issuer, out var realmId)) return null;
            // Reload fresh: the assertion is a five-minute-old statement, but identity is answered as of now.
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, CancellationToken.None);
            return user is null || user.Disabled || user.RealmId != realmId ? null : user;
        }

        // ValidateAnyAsync loads the user and refuses a disabled one, so its result is already fresh.
        var session = await sessions.ValidateAnyAsync(
            http.Request.Cookies[AuthSessionService.AccessCookieName], CancellationToken.None);
        return session?.User;
    }

    /// <summary>The token from an <c>Authorization: Bearer …</c> header, or <see langword="null"/> when absent.</summary>
    private static string? ExtractBearerToken(string? authorization) {
        if (string.IsNullOrEmpty(authorization)) return null;
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    // ── Applications portal ────────────────────────────────────────────────────

    /// <summary>
    /// The applications the calling account may enter, for the SPA's "Your applications" landing page — the
    /// thing a signed-in user of a non-operator realm gets instead of the management UI they are refused
    /// (design.md §13).
    /// </summary>
    /// <remarks>
    /// A plain endpoint on the any-realm surface rather than a JSON-RPC handler, and deliberately so: the
    /// <c>/rpc</c> surface is the operator population's (<c>SystemRealmAuthorizer</c>), and the boundary
    /// between "manage the instance" and "ask about yourself" is the point of the design. It works for an
    /// operator too — the policy below answers for any realm, the system realm included.
    /// </remarks>
    private static void MapApps(WebApplication app) {
        app.MapGet(AppsApiPath, AppsAsync);
    }

    /// <summary>
    /// Answers with the applications the caller may enter, or 401 when the <c>__wt_sso</c> cookie does not
    /// resolve to a live session.
    /// </summary>
    /// <remarks>
    /// <b>Nothing outside the caller's own population is named.</b> Two filters, in this order and both
    /// narrowing:
    /// <list type="number">
    ///   <item><description>
    ///     <see cref="RouteAccessPolicy.AccessibleRouteIdsAsync"/> — the same reading of accessibility
    ///     verify uses, so there is no second notion of it to drift with.
    ///   </description></item>
    ///   <item><description>
    ///     the caller's realm, from the same <see cref="RouteAccessPolicy.RouteRealmIdsAsync"/> the policy
    ///     itself consults. An <em>intersection</em>, never a union: it can only ever remove entries the
    ///     policy allowed, so it cannot widen what this surface discloses.
    ///   </description></item>
    /// </list>
    /// The second filter exists because the first deliberately admits a
    /// <see cref="AccessMode.Public"/> route to everybody — no identity is consulted for one, so no
    /// population is either. That is right for "may this request pass" and wrong for "what shall I name to
    /// you": without it, any realm's account could read off every public domain this instance proxies,
    /// which is precisely the enumeration <c>GET /api/proxy/ask</c> answers 404 to protect on these same
    /// hosts. With it, the answer names only routes of the caller's own realm that they could already have
    /// reached by typing the address.
    /// <para>
    /// Validated with <see cref="AuthSessionService.ValidateAsync"/> — the same call
    /// <c>/api/auth/continue</c> makes, and kind-correct: an <c>__wt_access</c> token presented in the SSO
    /// cookie is not an SSO session and is refused. Unlike UserInfo this does <em>not</em> reach for the
    /// non-renewing <c>ValidateAnyAsync</c>, and the reason is worth writing down rather than leaving as an
    /// apparent inconsistency: this endpoint is served on the auth host, where
    /// <c>UseAuthentication</c> has already resolved the very same <c>__wt_sso</c> cookie through
    /// <see cref="AuthSessionService.ValidateAsync"/> before any endpoint runs. The sliding window has
    /// therefore already been renewed by the time we are called, and a second, non-renewing read here would
    /// buy no property at all — only the appearance of one. UserInfo's case is genuinely different: it
    /// reads the per-app <c>__wt_access</c> cookie, which no middleware touches.
    /// </para>
    /// </remarks>
    private static async Task<IResult> AppsAsync(
        HttpContext http,
        WatchtowerDbContext db,
        AuthSessionService sessions,
        CancellationToken ct) {
        // Not ct: validation also writes (the sliding renewal, and the delete of an expired row), and a
        // client that hangs up must not turn that into a cancellation out of the auth check.
        var session = await sessions.ValidateAsync(
            http.Request.Cookies[AuthSessionService.SsoCookieName], CancellationToken.None);
        if (session?.User is null) return Results.Unauthorized();

        // The whole table as a narrow projection, then the policy: the bulk form settles every route in one
        // indexed grants query, and Watchtower's scale is tens of routes, so there is nothing to paginate.
        var rows = await db.Routes.AsNoTracking()
            .Select(r => new AppRouteRow(
                r.Id, r.Domain, r.AccessMode, r.TlsEnabled, r.IsPrimary, r.StackId, r.ServiceName, r.Stack!.Name))
            .ToListAsync(ct);

        // Detached stand-ins rather than a widened projection: AccessibleRouteIdsAsync documents that it
        // reads Id and AccessMode and nothing else, so those are the only two set here. Domain and
        // ServiceName are placeholders present because the entity marks them `required`, never values the
        // policy consults — the real ones stay on the rows above.
        var candidates = rows
            .Select(r => new Route { Id = r.Id, AccessMode = r.AccessMode, Domain = "", ServiceName = "" })
            .ToList();
        var accessible = await RouteAccessPolicy.AccessibleRouteIdsAsync(db, candidates, session.UserId, ct);
        if (accessible.Count == 0) return Ok(http, []);

        var realmIds = await RouteAccessPolicy.RouteRealmIdsAsync(db, [.. accessible], ct);
        var mine = rows
            .Where(r => accessible.Contains(r.Id))
            .Where(r => realmIds.TryGetValue(r.Id, out var realmId) && realmId == session.User.RealmId);

        var apps = mine
            .GroupBy(r => (r.StackId, r.ServiceName))
            .SelectMany(PreferPrimary)
            .Select(r => new AppLinkDto(r.Domain, r.StackName, $"{(r.TlsEnabled ? "https" : "http")}://{r.Domain}/"))
            // Name first, domain to break ties — a deterministic order so the page does not reshuffle
            // between loads.
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Domain, StringComparer.Ordinal)
            .ToList();

        return Ok(http, apps);
    }

    /// <summary>
    /// One entry per entry point: its canonical domain when the caller can reach it, otherwise every domain
    /// of that entry point they can.
    /// </summary>
    /// <remarks>
    /// The grouping key is the stack <em>and the service</em>, because that pair is what an entry point
    /// actually is (<see cref="Route.ServiceName"/> names the container a domain forwards to). Two domains
    /// pointing at the same service are an <b>alias</b> — one application wearing a second name — and
    /// listing both would tell the visitor they have two apps when they have one. Two domains pointing at
    /// <em>different</em> services of one stack are not aliases at all but two ways in (a UI on
    /// <c>app.example.com</c>, its API on <c>api.example.com</c>), and collapsing those would hide one the
    /// caller may well be the only person granted. The card carries the domain under the stack's name, so
    /// two entry points of one stack stay distinguishable.
    /// <para>
    /// The fallback matters because <see cref="Route.IsPrimary"/> is a property of the stack's routes, not
    /// of the caller's: a Restricted estate may well grant somebody an alias and not the primary, and
    /// silently dropping the entry point in that case would hide an application they are entitled to.
    /// Showing the aliases is the honest degradation.
    /// </para>
    /// </remarks>
    private static IEnumerable<AppRouteRow> PreferPrimary(IGrouping<(int StackId, string ServiceName), AppRouteRow> entryPoint) {
        var primary = entryPoint.Where(r => r.IsPrimary).ToList();
        return primary.Count > 0 ? primary : entryPoint;
    }

    /// <summary>
    /// The success response. <c>no-store</c> because the body is per-account: a shared cache between the
    /// browser and here must not be able to hand one visitor's application list to the next one.
    /// </summary>
    private static IResult Ok(HttpContext http, IReadOnlyList<AppLinkDto> apps) {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new AppsResponse(apps));
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
    /// Says once per realm that its protected routes cannot redirect anywhere because it has no login host.
    /// Until one exists, every anonymous request to a protected app of that realm gets a bare 401 instead of
    /// the login page. The two realms are told apart because the fix is different: configuration for the
    /// operator realm, a <c>realms.update</c> for any other.
    /// </summary>
    private static void WarnMissingAuthHostOnce(ILoggerFactory loggerFactory, Realm realm) {
        if (!WarnedRealms.TryAdd(realm.Id, 0)) return;
        var logger = loggerFactory.CreateLogger(typeof(WatchtowerAccessEndpoints).FullName!);
        if (realm.IsSystem) {
            logger.LogWarning(
                "Auth:Host is not configured, so unauthenticated requests to protected apps are answered " +
                "with 401 instead of being redirected to the login page. Set Watchtower:Auth:Host to the " +
                "hostname the Watchtower UI is reachable on.");
        } else {
            logger.LogWarning(
                "Realm '{Realm}' has no auth host, so unauthenticated requests to its protected apps are " +
                "answered with 401 instead of being redirected to a login page. Set the realm's authHost " +
                "to the hostname its login page is reachable on.",
                realm.Slug);
        }
    }
}
