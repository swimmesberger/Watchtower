using System.Text;
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
/// Verify's <em>decision</em> is not here: it lives in <see cref="AccessVerifier"/>, and this endpoint is
/// the adapter that reads a request's shape off the <c>X-Forwarded-*</c> headers and renders the resulting
/// <see cref="AccessDecision"/> as a status, headers and — for a denial — a page. The same service answers
/// the in-process proxy without an HTTP hop, so the two transports cannot come to different verdicts about
/// who may enter an app; see ADR-0022.
/// </para>
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

    /// <summary>Audit kind written when a login code is exchanged for an app session.</summary>
    private const string CodeRedeemed = "code.redeemed";

    /// <summary>OIDC UserInfo (OpenID Connect Core 1.0 §5.3), served on the auth host for bearer callers.</summary>
    private const string UserInfoApiPath = "/api/access/userinfo";

    /// <summary>The same UserInfo handler on every protected app's own domain, for same-origin cookie callers.</summary>
    private const string UserInfoAppPath = "/.watchtower/userinfo";

    /// <summary>The applications the calling account may enter, served on the auth host for the SPA portal.</summary>
    private const string AppsApiPath = "/api/access/apps";

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
    /// <remarks>
    /// An adapter and nothing more: it reads the request's shape off the forwarded headers, hands it to
    /// <see cref="AccessVerifier"/>, and renders the <see cref="AccessDecision"/> as the status, headers and
    /// page Caddy expects. The decision itself is shared with the in-process proxy, which asks the same
    /// service without an HTTP hop — see ADR-0022.
    /// </remarks>
    private static void MapVerify(WebApplication app) {
        app.MapGet(RouteAccessPolicy.VerifyPath, async (
            HttpContext http,
            AccessVerifier verifier,
            CancellationToken ct) => {

            var decision = await verifier.DecideAsync(new AccessRequest(
                Host: http.Request.Headers["X-Forwarded-Host"],
                OriginalUri: http.Request.Headers["X-Forwarded-Uri"],
                AccessCookie: http.Request.Cookies[AuthSessionService.AccessCookieName],
                IsBrowserNavigation: IsBrowserNavigation(http),
                ClientDescription: Describe(http)), ct);

            switch (decision) {
                case AccessDecision.Pass:
                    return Results.Ok();
                case AccessDecision.Allow allow:
                    // Onto the *response*: forward_auth's copy_headers is what moves them onto the proxied
                    // request, so this is where the upstream's identity headers are handed over.
                    foreach (var (name, value) in allow.Headers) http.Response.Headers[name] = value;
                    return Results.Ok();
                case AccessDecision.RedirectToLogin redirect:
                    return Results.Redirect(redirect.Url);
                case AccessDecision.Unauthorized:
                    return Results.Unauthorized();
                case AccessDecision.Denied denied:
                    // The decision states the message as plain text; escaping it is the renderer's job.
                    return Html(
                        StatusCodes.Status403Forbidden, denied.Title, Encode(denied.Message), denied.Hint);
                case AccessDecision.NotFound:
                    return Results.NotFound();
                default:
                    // Every case is answered above, so reaching here means a new one was added to
                    // AccessDecision without deciding what this transport makes of it. Failing loudly is the
                    // only safe direction: a fall-through 404 would quietly turn an unhandled verdict into
                    // "not a Watchtower app", which is a 4xx Caddy hands straight to the visitor.
                    throw new InvalidOperationException(
                        $"Unhandled access decision {decision.GetType().Name}.");
            }
        });
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
            // A route with no hostname cannot match one either: a port route (ADR-0033) is Public by
            // construction, so no code was ever minted against it, and the comparison states that rather
            // than relying on it.
            var host = RouteAccessPolicy.NormalizeForwardedHost(http.Request.Headers["X-Forwarded-Host"]);
            if (host is null || route.Domain is not { } domain
                || !string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
                return ExpiredCodePage();

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == grant.UserId, ct);
            if (user is null || user.Disabled) return ExpiredCodePage();

            if (route.AccessMode == AccessMode.Public ||
                !await RouteAccessPolicy.IsAuthorizedAsync(db, route, user.Id, ct)) {
                return Html(StatusCodes.Status403Forbidden, "Access denied",
                    $"Your account is not permitted to use {Encode(domain)}.",
                    "Ask an administrator to grant you access.");
            }

            var token = await sessions.CreateAppSessionAsync(user, route.Id, CancellationToken.None);
            AuthCookies.Append(
                http, AuthSessionService.AccessCookieName, token,
                sessions.AbsoluteLifetime, options.CurrentValue.Auth.CookieSecure);

            await AuthAudit.QueueAsync(db, time, CodeRedeemed, user.Id, route.Id, Describe(http), target: domain);
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
        // Watchtower's own routes are excluded outright (ADR-0023): the portal names applications a visitor
        // can be sent to, and the page they are already looking at is not one of them — it is where this
        // list is rendered.
        // A port route (ADR-0033) is left out with them, and for the same reason: the portal renders an
        // address a visitor can navigate to, and a route addressed by a listener on this host has none.
        var projected = await db.Routes.AsNoTracking()
            .Where(r => r.Target == RouteTarget.Service && r.Binding == RouteBinding.Domain)
            .Select(r => new {
                r.Id,
                r.Domain,
                r.AccessMode,
                r.TlsEnabled,
                r.IsPrimary,
                StackId = r.StackId!.Value,
                r.ServiceName,
                StackName = r.Stack!.Name,
            })
            .ToListAsync(ct);

        var rows = new List<AppRouteRow>(projected.Count);
        foreach (var r in projected) {
            // The filter above already settled this; the row is built from a hostname, so it is stated.
            if (r.Domain is not { } domain) continue;
            rows.Add(new AppRouteRow(
                r.Id, domain, r.AccessMode, r.TlsEnabled, r.IsPrimary, r.StackId, r.ServiceName, r.StackName));
        }

        // Detached stand-ins rather than a widened projection: AccessibleRouteIdsAsync documents that it
        // reads Id and AccessMode and nothing else, so those are the only two set here. ServiceName is a
        // placeholder present because the entity marks it `required`, never a value the policy consults —
        // the real ones stay on the rows above.
        var candidates = rows
            .Select(r => new Route { Id = r.Id, AccessMode = r.AccessMode, ServiceName = "" })
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
    /// The denial page as an <see cref="IResult"/>. The markup itself comes from
    /// <see cref="AccessPresentation.Html"/>, which the in-process dispatcher renders too — a visitor
    /// refused by either transport sees the same page.
    /// </summary>
    /// <param name="messageHtml">
    /// The one interpolated fragment, and therefore the caller's responsibility: any value that is not a
    /// literal must already have been through <see cref="Encode"/>.
    /// </param>
    private static IResult Html(int statusCode, string title, string messageHtml, string hint) =>
        Results.Content(AccessPresentation.Html(title, messageHtml, hint), "text/html", Encoding.UTF8, statusCode);

    private static string Encode(string value) => AccessPresentation.Encode(value);

    private static bool IsBrowserNavigation(HttpContext http) => AccessPresentation.IsBrowserNavigation(http);

    /// <summary>Audit detail: the remote address, never a cookie or a code.</summary>
    private static string Describe(HttpContext http) => AccessPresentation.Describe(http);
}
