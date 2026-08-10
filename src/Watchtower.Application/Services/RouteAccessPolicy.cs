using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The decisions the forward-auth surface makes about a route, in one place because three endpoints have
/// to agree on them: verify (may this request pass?), login and continue (may this account be handed a
/// code for that app?). A disagreement between them would be a hole, not a bug.
/// </summary>
/// <remarks>
/// Everything here is deliberately fail-closed: an input that cannot be understood is not exempt, not a
/// known host and not authorised.
/// </remarks>
public static class RouteAccessPolicy {
    /// <summary>
    /// Path prefix reserved for Watchtower's own plumbing on every protected app's domain (callback and
    /// per-app logout). Caddy routes it to Watchtower rather than the upstream, so an app that genuinely
    /// wants this prefix cannot have it — the same trade Cloudflare makes with <c>/cdn-cgi/</c>.
    /// </summary>
    public const string ReservedPathPrefix = "/.watchtower/";

    /// <summary>Code-redemption endpoint, served on the app's own domain.</summary>
    public const string CallbackPath = "/.watchtower/callback";

    /// <summary>Per-app sign-out, served on the app's own domain.</summary>
    public const string AppLogoutPath = "/.watchtower/logout";

    /// <summary>The <c>forward_auth</c> target Caddy consults for every request to a protected app.</summary>
    public const string VerifyPath = "/api/access/verify";

    /// <summary>
    /// The ES256 assertion forwarded to the upstream (<see cref="AuthTokenSigner"/>). Always emitted for a
    /// protected route — it is the source of truth for identity forwarding, not a mode-gated convenience.
    /// The plaintext-header vocabulary and the defense-in-depth strip set live in <see cref="IdentityForwarding"/>.
    /// </summary>
    public const string JwtHeaderName = "X-Watchtower-Jwt";

    /// <summary>
    /// Characters that must never appear in a forwarded host. A value carrying any of them is something
    /// other than a host name — a URL, a credential, or an attempt to have the parser read it as one.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> ForbiddenHostChars =
        System.Buffers.SearchValues.Create("/\\@?# \t");

    /// <summary>
    /// Normalises an <c>X-Forwarded-Host</c> header to the form route domains are stored in, or
    /// <see langword="null"/> when it is missing or not a plain host name.
    /// </summary>
    /// <remarks>
    /// Only the first entry of a comma-separated list is considered, any port is dropped, and a value
    /// carrying a path, userinfo or whitespace is rejected outright rather than coerced — the header is
    /// attacker-reachable, and the one thing it must never do is resolve to a route it does not name.
    /// </remarks>
    public static string? NormalizeForwardedHost(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var first = value.Split(',')[0].Trim();
        if (first.Length == 0) return null;
        if (first.AsSpan().IndexOfAny(ForbiddenHostChars) >= 0) return null;
        if (!Uri.TryCreate($"https://{first}/", UriKind.Absolute, out var parsed)) return null;

        var host = parsed.IdnHost;
        return string.IsNullOrEmpty(host) ? null : host.ToLowerInvariant();
    }

    /// <summary>Looks a route up by domain, case-insensitively. Returns <see langword="null"/> for an unknown host.</summary>
    public static Task<Route?> FindRouteByHostAsync(WatchtowerDbContext db, string host, CancellationToken ct) =>
        db.Routes.AsNoTracking().FirstOrDefaultAsync(r => r.Domain.ToLower() == host, ct);

    /// <summary>One route's realm as the database can project it: null means "standalone stack".</summary>
    private sealed record RouteRealmRow(int RouteId, int? TemplateRealmId);

    /// <summary>
    /// The realm each of <paramref name="routeIds"/> belongs to, as one query
    /// (docs/central-auth/design.md §13). A route inherits its realm from its stack's category
    /// (<see cref="StackTemplate.RealmId"/>); a standalone stack has no category, so its routes belong to
    /// the system realm.
    /// </summary>
    /// <remarks>
    /// Routes carry no realm column of their own on purpose: the category is where a population is decided,
    /// and duplicating it onto every tenant route would be one more thing that can disagree with it. Ids
    /// that name no route are simply absent from the result, which is what lets a caller tell "belongs to
    /// the system realm" from "there is no such route".
    /// </remarks>
    public static async Task<IReadOnlyDictionary<int, int>> RouteRealmIdsAsync(
        WatchtowerDbContext db, IReadOnlyCollection<int> routeIds, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(routeIds);
        if (routeIds.Count == 0) return new Dictionary<int, int>();

        var rows = await db.Routes.AsNoTracking()
            .Where(r => routeIds.Contains(r.Id))
            .Select(r => new RouteRealmRow(r.Id, (int?)r.Stack!.Template!.RealmId))
            .ToListAsync(ct);

        return rows.ToDictionary(x => x.RouteId, x => x.TemplateRealmId ?? Realm.SystemRealmId);
    }

    /// <summary>
    /// The realm one route belongs to, or <see langword="null"/> when there is no such route.
    /// </summary>
    public static async Task<int?> RouteRealmIdAsync(WatchtowerDbContext db, int routeId, CancellationToken ct) {
        var realms = await RouteRealmIdsAsync(db, [routeId], ct);
        return realms.TryGetValue(routeId, out var realmId) ? realmId : null;
    }

    /// <summary>The path portion of an <c>X-Forwarded-Uri</c>, with the query string and fragment removed.</summary>
    public static string ExtractPath(string? forwardedUri) {
        if (string.IsNullOrEmpty(forwardedUri)) return "/";
        var span = forwardedUri.AsSpan();
        var cut = span.IndexOfAny('?', '#');
        if (cut >= 0) span = span[..cut];
        return span.IsEmpty ? "/" : span.ToString();
    }

    /// <summary>
    /// Parses the newline-separated bypass list, dropping blanks and any entry that is not a rooted path
    /// (a prefix that cannot occur in a request path would only ever be dead configuration).
    /// </summary>
    public static IEnumerable<string> ParseBypassPaths(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var line in raw.Split('\n')) {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '/') continue;
            yield return trimmed;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is exempt from access control on a route configured with
    /// <paramref name="bypassPaths"/> — either a reserved Watchtower path or a configured bypass prefix.
    /// </summary>
    /// <remarks>
    /// Two guards, both fail-closed, because the exemption is decided on the <em>raw</em> forwarded path
    /// while the upstream acts on whatever it makes of that path after decoding and normalising — and
    /// Watchtower does neither, so it cannot predict the result:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Any percent-encoding disqualifies the exemption.</b> A literal ASCII bypass prefix like
    ///     <c>/webhooks/</c> needs no encoding to be matched by a legitimate request, so an encoded byte in
    ///     the matched path is only ever an attempt to smuggle something past the prefix check: with a
    ///     <c>/webhooks/</c> bypass, <c>/webhooks/..%2fadmin</c> keeps the dots literal <em>and</em> hides
    ///     the separator, so no segment equals <c>..</c> yet an upstream that decodes <c>%2f</c>→<c>/</c>
    ///     and normalises reaches <c>/admin</c> unauthenticated. We cannot know which upstreams decode, so
    ///     the presence of a single <c>%</c> is enough to fall through to the full check.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A literal dot segment disqualifies it too</b> — <c>/public/../admin</c> would otherwise match
    ///     a <c>/public/</c> bypass and then reach <c>/admin</c>. (The <c>%</c> rule already covers the
    ///     encoded forms; this stays for the un-encoded ones.)
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static bool IsExemptPath(string? bypassPaths, string path) {
        // The encoded-byte guard runs first and covers %2e/%2f/%5c and every other encoded form in one
        // rule; HasDotSegment then handles the un-encoded `..`/`.` segments.
        if (path.Contains('%', StringComparison.Ordinal)) return false;
        if (HasDotSegment(path)) return false;
        if (path.StartsWith(ReservedPathPrefix, StringComparison.Ordinal)) return true;

        foreach (var prefix in ParseBypassPaths(bypassPaths))
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>True when the path contains a <c>.</c> or <c>..</c> segment, literally or percent-encoded.</summary>
    public static bool HasDotSegment(string path) {
        if (path.Contains("%2e", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var segment in path.Split('/'))
            if (segment is "." or "..") return true;
        return false;
    }

    /// <summary>What a route's <see cref="AccessMode"/> means for a signed-in account.</summary>
    private enum AccessDecision {
        /// <summary>Not accessible, and nothing to look up — the fail-closed answer for an unknown mode.</summary>
        Deny,
        /// <summary>Not access-controlled at all: no identity is consulted, so no realm is either.</summary>
        Unprotected,
        /// <summary>Accessible to any signed-in account <em>of the route's realm</em>.</summary>
        Authenticated,
        /// <summary>Accessible with a <see cref="RouteAccessGrant"/>, and only from the route's realm.</summary>
        RequiresGrant,
    }

    /// <summary>
    /// The single reading of <see cref="AccessMode"/> that both authorisation entry points below share, so
    /// the per-route and the bulk answer cannot drift apart — a disagreement between them would be a hole.
    /// </summary>
    /// <remarks>
    /// <see cref="AccessMode.Public"/> is separated from <see cref="AccessMode.Authenticated"/> rather than
    /// folded in with it (as it was before realms) precisely because the realm invariant applies to one and
    /// not the other: a public route asks nobody who they are, so there is no population to compare.
    /// </remarks>
    private static AccessDecision Classify(AccessMode mode) => mode switch {
        AccessMode.Public => AccessDecision.Unprotected,
        AccessMode.Authenticated => AccessDecision.Authenticated,
        AccessMode.Restricted => AccessDecision.RequiresGrant,
        // A mode this build does not know about is not a licence to let the request through.
        _ => AccessDecision.Deny,
    };

    /// <summary>
    /// The realm invariant, written once (docs/central-auth/design.md §13): <b>a protected route is only
    /// ever reachable by an account of its own realm</b>, whatever its grants say.
    /// </summary>
    /// <remarks>
    /// This is the structural half of realm isolation, and the reason it lives here rather than in the
    /// endpoints: both entry points below run it from the same expression, so "may this account enter this
    /// route" cannot be answered one way per route and another way in bulk. A grant naming a foreign
    /// account is not a special case — it simply does not admit anyone, so a stale grant left behind by a
    /// realm change grants nothing rather than crossing the boundary.
    /// <para>
    /// Deliberately indistinguishable from "holds no grant" to the caller: both entry points return the
    /// same <see langword="false"/>, and every surface above collapses that into one refusal, so a realm
    /// mismatch cannot be told apart from an absent grant (design.md §5, the anti-enumeration property).
    /// </para>
    /// </remarks>
    private static bool RealmAdmits(int? userRealmId, int? routeRealmId) =>
        userRealmId is { } user && routeRealmId is { } route && user == route;

    /// <summary>The realm an account belongs to, or <see langword="null"/> when there is no such account.</summary>
    private static async Task<int?> UserRealmIdAsync(WatchtowerDbContext db, int userId, CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (int?)u.RealmId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The one reading of "this grant lets that account in", shared by both authorisation entry points so
    /// they cannot drift: a grant matches when it names the account directly, or when it names a group the
    /// account is a member of. Group membership is folded into the grant query itself rather than resolved
    /// first, so the per-request cost stays one round trip whichever kind of grant is in play.
    /// </summary>
    /// <remarks>
    /// The <c>GroupId != null</c> guard is not redundant with the membership subquery: it keeps a
    /// user-subject row out of the correlated lookup entirely, so the common case is decided on the grant
    /// row alone.
    /// </remarks>
    private static Expression<Func<RouteAccessGrant, bool>> GrantAdmits(WatchtowerDbContext db, int userId) =>
        g => g.UserId == userId
             || (g.GroupId != null && db.GroupMembers.Any(m => m.GroupId == g.GroupId && m.UserId == userId));

    /// <summary>
    /// Whether <paramref name="userId"/> may enter <paramref name="route"/>. The account being valid and
    /// enabled is the caller's business; this answers only the policy question.
    /// </summary>
    public static async Task<bool> IsAuthorizedAsync(
        WatchtowerDbContext db, Route route, int userId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(route);

        var decision = Classify(route.AccessMode);
        if (decision == AccessDecision.Unprotected) return true;
        if (decision == AccessDecision.Deny) return false;

        // Protected: the realm invariant applies before any grant is consulted, so a foreign account is
        // refused whether or not somebody wrote it a grant row.
        if (!RealmAdmits(
                await UserRealmIdAsync(db, userId, ct),
                await RouteRealmIdAsync(db, route.Id, ct))) {
            return false;
        }

        return decision == AccessDecision.Authenticated || await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.RouteId == route.Id)
            .Where(GrantAdmits(db, userId))
            .AnyAsync(ct);
    }

    /// <summary>
    /// Which of <paramref name="routes"/> <paramref name="userId"/> may enter, as the set of their ids.
    /// </summary>
    /// <remarks>
    /// The bulk form of <see cref="IsAuthorizedAsync"/>, and deliberately not a loop over it: the entire
    /// restricted subset is settled by one indexed grants query, so answering for a template with fifty
    /// tenants costs a single round trip rather than fifty. The set shape is what makes that structural —
    /// a caller holding the whole answer has nothing left to ask per route. The realm invariant is applied
    /// in bulk for the same reason, and from the same <see cref="RealmAdmits"/> expression the per-route
    /// answer uses.
    /// </remarks>
    /// <param name="db">Database context to read grants through.</param>
    /// <param name="routes">Candidate routes; duplicates and unknown modes are harmless.</param>
    /// <param name="userId">The account being evaluated, already established as live and enabled.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ids of the routes the account may enter; empty when it may enter none.</returns>
    public static async Task<IReadOnlySet<int>> AccessibleRouteIdsAsync(
        WatchtowerDbContext db, IReadOnlyList<Route> routes, int userId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(routes);

        var accessible = new HashSet<int>();
        var guarded = new List<Route>();
        foreach (var route in routes) {
            switch (Classify(route.AccessMode)) {
                case AccessDecision.Unprotected:
                    accessible.Add(route.Id);
                    break;
                case AccessDecision.Authenticated:
                case AccessDecision.RequiresGrant:
                    guarded.Add(route);
                    break;
                default:
                    // Fail-closed: an unrecognised mode contributes nothing, not even a grant lookup.
                    break;
            }
        }

        if (guarded.Count == 0) return accessible;

        // Two reads for the whole set, not two per route: the account's realm, and every candidate route's.
        var userRealmId = await UserRealmIdAsync(db, userId, ct);
        var routeRealmIds = await RouteRealmIdsAsync(db, [.. guarded.Select(r => r.Id).Distinct()], ct);

        var restricted = new List<int>();
        foreach (var route in guarded) {
            if (!RealmAdmits(userRealmId, routeRealmIds.TryGetValue(route.Id, out var realmId) ? realmId : null))
                continue;
            if (Classify(route.AccessMode) == AccessDecision.Authenticated) accessible.Add(route.Id);
            else restricted.Add(route.Id);
        }

        if (restricted.Count == 0) return accessible;

        var granted = await db.RouteAccessGrants.AsNoTracking()
            .Where(g => restricted.Contains(g.RouteId))
            .Where(GrantAdmits(db, userId))
            .Select(g => g.RouteId)
            .ToListAsync(ct);
        accessible.UnionWith(granted);
        return accessible;
    }

    /// <summary>
    /// Parses a candidate <c>redirect_uri</c> and returns it in normalised form, or <see langword="null"/>
    /// when it is not an absolute <c>https</c> URL naming a bare host on the default port.
    /// </summary>
    /// <remarks>
    /// The scheme is fixed rather than inferred: the value arrives from the browser's address bar by way of
    /// the verify redirect, so allowing <c>http</c> (or userinfo, or a port) would widen what "the app's own
    /// domain" means. The caller still has to match the host against the route table — this only rejects
    /// shapes that could never be one.
    /// </remarks>
    public static Uri? ParseAppRedirectUri(string? candidate) {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (!uri.IsDefaultPort) return null;
        return string.IsNullOrEmpty(uri.IdnHost) ? null : uri;
    }

    /// <summary>
    /// Resolves a candidate <c>redirect_uri</c> to the protected route it belongs to, or
    /// <see langword="null"/>. Enforces the open-redirect guard (design.md §9): the host must be a route
    /// Watchtower actually serves, and that route must not be <see cref="AccessMode.Public"/> — a public
    /// app has no session to hand over, so a code minted for one would only be a redirect primitive.
    /// </summary>
    public static async Task<(Route Route, string Url)?> ResolveRedirectTargetAsync(
        WatchtowerDbContext db, string? candidate, CancellationToken ct) {
        var uri = ParseAppRedirectUri(candidate);
        if (uri is null) return null;

        var route = await FindRouteByHostAsync(db, uri.IdnHost.ToLowerInvariant(), ct);
        if (route is null || route.AccessMode == AccessMode.Public) return null;

        // The re-serialised absolute URI, not the caller's string: what is stored and later redirected to
        // is then something this parser produced, never raw input echoed back into a Location header.
        return (route, uri.AbsoluteUri);
    }
}
