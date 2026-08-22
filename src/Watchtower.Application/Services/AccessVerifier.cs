using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

// Watchtower's own entity, not Microsoft.AspNetCore.Routing.Route.
using Route = Watchtower.Application.Entities.Route;

namespace Watchtower.Application.Services;

/// <summary>
/// Everything the access decision needs about one request, independent of how that request arrived: the
/// Caddy <c>forward_auth</c> endpoint reads these off <c>X-Forwarded-*</c> headers, while an in-process
/// proxy reads them off the real request it is about to forward.
/// </summary>
/// <param name="Host">
/// The app host — <c>X-Forwarded-Host</c> at the endpoint, <c>Request.Host</c> in-process. Normalised here,
/// never trusted as written.
/// </param>
/// <param name="OriginalUri">
/// The original path and query — <c>X-Forwarded-Uri</c> at the endpoint, path + query string in-process.
/// </param>
/// <param name="AccessCookie">The <c>__wt_access</c> cookie value, if the request carried one.</param>
/// <param name="IsBrowserNavigation">
/// Whether this is a request the visitor would follow with their eyes (a document fetch), which is what
/// decides between a login redirect and a bare 401. Computed by the caller, because the two callers derive
/// it from different places: the forwarded method and <c>Accept</c> at the endpoint, the real ones in-process.
/// </param>
/// <param name="ClientDescription">The audit trail's description of the caller — the remote address, never a credential.</param>
public readonly record struct AccessRequest(
    string? Host,
    string? OriginalUri,
    string? AccessCookie,
    bool IsBrowserNavigation,
    string ClientDescription);

/// <summary>
/// What <see cref="AccessVerifier"/> decided about one request. Deliberately a description rather than a
/// response: the endpoint turns it into a status code and headers for Caddy, and an in-process proxy turns
/// the same value into "forward it" or "answer it here" without an HTTP hop in between.
/// </summary>
public abstract record AccessDecision {
    /// <summary>
    /// Closes the hierarchy to this assembly: the cases below are the whole vocabulary, and a caller
    /// switching over them exhaustively can rely on that.
    /// </summary>
    private protected AccessDecision() { }

    /// <summary>Let it through, carrying no identity — a public route, or an exempt path.</summary>
    public sealed record Pass : AccessDecision {
        /// <summary>The single instance; the case carries no data.</summary>
        public static Pass Instance { get; } = new();
    }

    /// <summary>
    /// Let it through as an identified account, with <paramref name="Headers"/> set on the way in — the
    /// signed assertion always, plus whatever the route's <see cref="IdentityHeaderMode"/> adds, in order.
    /// </summary>
    public sealed record Allow(IReadOnlyList<KeyValuePair<string, string>> Headers) : AccessDecision;

    /// <summary>Send the visitor to their realm's login page at <paramref name="Url"/>.</summary>
    public sealed record RedirectToLogin(string Url) : AccessDecision;

    /// <summary>Refuse without a login page: not a navigation, or the realm has nowhere to send them.</summary>
    public sealed record Unauthorized : AccessDecision {
        /// <summary>The single instance; the case carries no data.</summary>
        public static Unauthorized Instance { get; } = new();
    }

    /// <summary>
    /// Signed in, but not permitted here. The three strings are the denial page's plain text — <b>not</b>
    /// HTML — so a caller rendering a page is the one that escapes them.
    /// </summary>
    public sealed record Denied(string Title, string Message, string Hint) : AccessDecision;

    /// <summary>No route answers to that host, so this is not a Watchtower app at all.</summary>
    public sealed record NotFound : AccessDecision {
        /// <summary>The single instance; the case carries no data.</summary>
        public static NotFound Instance { get; } = new();
    }
}

/// <summary>
/// The forward-auth decision itself (docs/central-auth/design.md §5): given a request's host, path, cookie
/// and shape, may it enter — and as whom? Lives here rather than in the endpoint because two transports have
/// to reach the same verdict: Caddy's <c>forward_auth</c> hop to <c>GET /api/access/verify</c>, and the
/// in-process proxy, which asks this service directly (see ADR-0020). A second implementation
/// of "may this request pass" would be a hole, not a bug — the same reasoning that puts
/// <see cref="RouteAccessPolicy"/> in one place.
/// </summary>
/// <remarks>
/// Scoped, like the context it reads through. It answers with an <see cref="AccessDecision"/> and touches no
/// response: nothing here knows about status codes, HTML or header collections, which is exactly what lets
/// the in-process caller act on the verdict without inventing an HTTP exchange to carry it.
/// </remarks>
public sealed class AccessVerifier(
    WatchtowerDbContext db,
    AuthSessionService sessions,
    AuthTokenSigner signer,
    RealmResolver realms,
    TimeProvider time,
    ILoggerFactory loggerFactory) {
    /// <summary>
    /// Caps the length of the <c>redirect_uri</c> echoed into the login redirect. A caller controls the
    /// original URI, and an unbounded value would become an unbounded <c>Location</c> header.
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
    /// Decides whether <paramref name="request"/> may enter the app its host names, and as whom.
    /// </summary>
    /// <remarks>
    /// The order of the steps is the contract: the host resolves a route, a public route or an exempt path
    /// short-circuits before any identity is considered, the per-app session is then resolved, and only an
    /// authorised account reaches the point where identity headers are built.
    /// </remarks>
    public async Task<AccessDecision> DecideAsync(AccessRequest request, CancellationToken ct) {
        // 1. Which app is this? The host names the route; an unknown one is not a Watchtower app.
        var host = RouteAccessPolicy.NormalizeForwardedHost(request.Host);
        if (host is null) return AccessDecision.NotFound.Instance;

        var route = await RouteAccessPolicy.FindRouteByHostAsync(db, host, ct);
        if (route is null) return AccessDecision.NotFound.Instance;

        // No forward_auth is emitted for a public route, so reaching here means the config is stale.
        // Letting the request through matches what the proxy would have done without us.
        if (route.AccessMode == AccessMode.Public) return AccessDecision.Pass.Instance;

        // 2. Exempt paths answer before any identity is considered, and carry no identity headers —
        //    a bypass path is "no access control here", not "anonymous access as somebody".
        var path = RouteAccessPolicy.ExtractPath(request.OriginalUri);
        if (RouteAccessPolicy.IsExemptPath(route.BypassPaths, path)) return AccessDecision.Pass.Instance;

        // 3. The per-app session. Deliberately not ct: validation may renew the sliding window, and a
        //    client that hangs up must not turn that write into a cancellation out of the auth check.
        var session = await sessions.ValidateAppSessionAsync(
            request.AccessCookie, route.Id, CancellationToken.None);

        // The realm is loaded with the account, so an authorised request costs no extra read for it;
        // the anonymous branch is the one that has to go and ask which population owns this route.
        if (session?.User?.Realm is null) return await ChallengeAnonymousAsync(route, request, ct);

        // 4. Signed in, but policy may still refuse this app — including because the account belongs to
        //    another realm, which IsAuthorizedAsync folds into the same single refusal as a missing grant.
        if (!await RouteAccessPolicy.IsAuthorizedAsync(db, route, session.UserId, ct))
            return await DenyAsync(route, session.UserId, request.ClientDescription);

        // 5. Authorised. One membership read feeds both forwarding channels — see IdentityHeaders.
        //    The account's realm is the route's realm by the check above, so it is what the assertion is
        //    minted for.
        var groups = await GroupMembership.NamesAsync(db, session.UserId, ct);
        return new AccessDecision.Allow(
            IdentityHeaders(
                session.User, route, await realms.IdentityForAsync(session.User.Realm, ct), groups));
    }

    /// <summary>
    /// What an unauthenticated request gets: a browser navigation is sent to the central login page, and
    /// everything else gets a plain 401 — redirecting an XHR or a POST into a login form would turn a
    /// clean failure into a mystery, and would replay the body nowhere useful.
    /// </summary>
    /// <remarks>
    /// The login host is <em>the route's realm's</em> (docs/central-auth/design.md §13): the domain of the
    /// realm's login <see cref="Route"/>, falling back to the configured <c>Auth:Host</c> on the system
    /// realm only (ADR-0021) — so a visitor is only ever sent to the login page of the population that could
    /// actually admit them. A realm with no login route yet has no host, and its routes then fail closed
    /// with a bare 401 rather than redirecting somewhere arbitrary, exactly as an instance with no
    /// <c>Auth:Host</c> already did.
    /// <para>
    /// The redirect is assembled from <em>stored</em> values: literal <c>https</c>, the realm's login host,
    /// and the route's own domain. The forwarded scheme and host never reach the target — the only
    /// caller-supplied part is the path, which is bounded, required to be rooted, and percent-encoded into a
    /// query parameter.
    /// </para>
    /// </remarks>
    private async Task<AccessDecision> ChallengeAnonymousAsync(Route route, AccessRequest request, CancellationToken ct) {
        var realm = await realms.RealmForRouteAsync(route, ct);
        var loginHost = await realms.LoginHostForAsync(realm, ct);
        if (loginHost is null) {
            WarnMissingLoginHostOnce(realm);
            return AccessDecision.Unauthorized.Instance;
        }

        if (!request.IsBrowserNavigation) return AccessDecision.Unauthorized.Instance;

        var original = $"https://{route.Domain}{OriginalPathAndQuery(request.OriginalUri)}";
        return new AccessDecision.RedirectToLogin(
            $"https://{loginHost}/login?redirect_uri={Uri.EscapeDataString(original)}");
    }

    /// <summary>The caller-supplied part of the original URL, or <c>/</c> when it is missing or unusable.</summary>
    private static string OriginalPathAndQuery(string? originalUri) =>
        string.IsNullOrEmpty(originalUri) || originalUri[0] != '/' || originalUri.Length > MaxOriginalUriLength
            ? "/"
            : originalUri;

    /// <summary>
    /// Authenticated but not authorised. Recorded, then answered with the denial page rather than a
    /// redirect: sending them back to a login they have already completed would loop.
    /// </summary>
    private async Task<AccessDecision> DenyAsync(Route route, int userId, string? clientDescription) {
        // Coalesced rather than passed through: AccessRequest is a struct, so a default one carries a null
        // description, and a denial row with no detail at all is worse than one that says nothing.
        await AuthAudit.QueueAsync(db, time, AuthEventKinds.AccessDenied, userId, route.Id,
            clientDescription ?? "", success: false, target: route.Domain);
        // Not RequestAborted: a caller that disconnects must not be able to keep denials out of the trail.
        await db.SaveChangesAsync(CancellationToken.None);

        return new AccessDecision.Denied("Access denied",
            $"You are signed in, but your account is not permitted to use {route.Domain}.",
            "Ask an administrator to grant you access.");
    }

    /// <summary>
    /// The identity headers a verified request carries in, in order. The signed assertion is <em>always</em>
    /// written — it is the source of truth (design.md §2.3). Plaintext convenience headers are added only
    /// when the route opted into a mode, under that mode's ecosystem-standard names, read from the
    /// single-source <see cref="IdentityForwarding"/> helper so the set can never drift from what the proxy
    /// strips and copies. Every forwardable name is stripped from the inbound request first, so what the
    /// upstream receives is only ever what is built here.
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
    private List<KeyValuePair<string, string>> IdentityHeaders(
        User user, Route route, RealmIdentity realm, IReadOnlyList<string> groups) {
        var headers = new List<KeyValuePair<string, string>>();

        // Source of truth, forwarded for every protected route regardless of mode. It carries the groups
        // even on a None route: the signed assertion is where a group-aware app should read them from.
        var assertion = signer.Mint(user, route.Domain, realm, groups);
        headers.Add(new KeyValuePair<string, string>(RouteAccessPolicy.JwtHeaderName, assertion));
        // Cloudflare mode: the same assertion also travels under Cloudflare's header name, so an app
        // written against Cf-Access-Jwt-Assertion only re-points its JWKS/issuer config at Watchtower.
        if (route.IdentityHeaderMode == IdentityHeaderMode.Cloudflare)
            headers.Add(new KeyValuePair<string, string>(IdentityForwarding.CfAccessJwtAssertion, assertion));

        // Plaintext convenience headers: only for a route that asked for them, and only values safe to put
        // in a header (the email and group entries are already omitted by the helper when there is nothing
        // to say). Group names are constrained to printable ASCII at creation time, so the joined value the
        // helper produces survives HeaderSafe intact.
        foreach (var (headerName, value) in IdentityForwarding.PlaintextHeaders(
                     route.IdentityHeaderMode, user.UserName, user.Email, groups)) {
            var safe = HeaderSafe(value);
            if (safe is not null) headers.Add(new KeyValuePair<string, string>(headerName, safe));
        }

        return headers;
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

    /// <summary>
    /// Says once per realm that its protected routes cannot redirect anywhere because it has no login host.
    /// Until one exists, every anonymous request to a protected app of that realm gets a bare 401 instead of
    /// the login page. The two realms are told apart because the fallback differs: the system realm also
    /// accepts a configured <c>Auth:Host</c>, so its message names both fixes.
    /// </summary>
    private void WarnMissingLoginHostOnce(Realm realm) {
        if (!WarnedRealms.TryAdd(realm.Id, 0)) return;
        var logger = loggerFactory.CreateLogger(typeof(AccessVerifier).FullName!);
        if (realm.IsSystem) {
            logger.LogWarning(
                "The operator realm has no login host, so unauthenticated requests to protected apps are " +
                "answered with 401 instead of being redirected to the login page. Create a Watchtower " +
                "route for the hostname the Watchtower UI is reachable on and mark it as the login host, " +
                "or set Watchtower:Auth:Host when another proxy serves Watchtower.");
        } else {
            logger.LogWarning(
                "Realm '{Realm}' has no login host, so unauthenticated requests to its protected apps are " +
                "answered with 401 instead of being redirected to a login page. Create a Watchtower route " +
                "in the realm and mark it as the realm's login host.",
                realm.Slug);
        }
    }
}
