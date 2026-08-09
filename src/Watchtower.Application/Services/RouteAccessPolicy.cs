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

    /// <summary>Verified user name forwarded to the upstream.</summary>
    public const string UserHeaderName = "X-Watchtower-User";

    /// <summary>Verified email forwarded to the upstream, when the account has one.</summary>
    public const string EmailHeaderName = "X-Watchtower-Email";

    /// <summary>The ES256 assertion forwarded to the upstream (<see cref="AuthTokenSigner"/>).</summary>
    public const string JwtHeaderName = "X-Watchtower-Jwt";

    /// <summary>
    /// Every header the verify endpoint may set. The generated Caddy config strips exactly this list from
    /// the inbound request and copies exactly this list back out, so both sides read it from here — a name
    /// that were copied but not stripped would be client-spoofable (design.md §2.3).
    /// </summary>
    public static readonly string[] IdentityHeaderNames = [UserHeaderName, EmailHeaderName, JwtHeaderName];

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
    /// A path containing a dot segment is never exempt, in any form. Prefix matching happens on the raw
    /// path, while the upstream sees whatever it makes of that path after normalisation; without this
    /// guard <c>/public/../admin</c> would match a <c>/public/</c> bypass and then reach <c>/admin</c>.
    /// Percent-encoded dots are rejected on sight for the same reason — Watchtower does not decode the
    /// path, so it cannot know what the upstream will make of <c>%2e%2e</c>, and guessing wrong here fails
    /// open.
    /// </remarks>
    public static bool IsExemptPath(string? bypassPaths, string path) {
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

    /// <summary>
    /// Whether <paramref name="userId"/> may enter <paramref name="route"/>. The account being valid and
    /// enabled is the caller's business; this answers only the policy question.
    /// </summary>
    public static async Task<bool> IsAuthorizedAsync(
        WatchtowerDbContext db, Route route, int userId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(route);
        return route.AccessMode switch {
            AccessMode.Public => true,
            AccessMode.Authenticated => true,
            AccessMode.Restricted => await db.RouteAccessGrants.AsNoTracking()
                .AnyAsync(g => g.RouteId == route.Id && g.UserId == userId, ct),
            // A mode this build does not know about is not a licence to let the request through.
            _ => false,
        };
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
