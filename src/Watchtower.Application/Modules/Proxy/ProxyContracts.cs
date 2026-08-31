using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>A public route projection for the API (enum fields lowercased for the client).</summary>
/// <param name="Target">
/// <c>service</c> or <c>watchtower</c> (ADR-0023). A <c>watchtower</c> route has no
/// <paramref name="StackId"/>, <paramref name="ServiceName"/> or <paramref name="ContainerPort"/> worth
/// showing, and carries a <paramref name="RealmId"/> instead.
/// </param>
/// <param name="RealmId">The realm a <c>watchtower</c> route serves; null on a <c>service</c> route.</param>
/// <param name="RealmSlug">That realm's slug, so a client can label the row without a second call.</param>
/// <param name="IsLoginRoute">
/// Whether the realm named this route as its login host — the address its protected apps redirect
/// anonymous visitors to.
/// </param>
/// <param name="Domain">
/// The route's hostname, or null for a port-bound route (ADR-0033), which is addressed by a listener of
/// its own instead. The binding and the listen port are not carried yet — the API surface for port
/// routes is the next stage; this field is nullable now because the column is.
/// </param>
public sealed record RouteDto(
    int Id,
    int? StackId,
    string? StackName,
    string? Domain,
    string ServiceName,
    int ContainerPort,
    bool TlsEnabled,
    bool IsPrimary,
    string Kind,
    string Status,
    string? StatusDetail,
    DateTimeOffset? CertNotAfter,
    DateTimeOffset CreatedAt,
    string Target,
    int? RealmId,
    string? RealmSlug,
    bool IsLoginRoute);

/// <summary>In-memory projection + validation helpers (not translatable to SQL).</summary>
public static class RouteMapping {
    /// <summary>
    /// Projects a route for the API. <paramref name="isLoginRoute"/> is passed in rather than read off a
    /// navigation because it is a fact about the <em>realm</em> (<see cref="Realm.LoginRouteId"/>), and the
    /// listing handler settles it for every row in one query.
    /// </summary>
    public static RouteDto ToDto(Route r, bool isLoginRoute = false) {
        ArgumentNullException.ThrowIfNull(r);
        return new RouteDto(
            r.Id, r.StackId, r.Stack?.Name, r.Domain, r.ServiceName, r.ContainerPort,
            r.TlsEnabled, r.IsPrimary,
            r.Kind.ToString().ToLowerInvariant(),
            r.Status.ToString().ToLowerInvariant(),
            r.StatusDetail, r.CertNotAfter, r.CreatedAt,
            r.Target.ToString().ToLowerInvariant(),
            r.RealmId,
            r.Realm?.Slug,
            isLoginRoute);
    }

    /// <summary>Normalizes a domain: trimmed and lowercased. Returns null when blank/whitespace.</summary>
    public static string? NormalizeDomain(string? domain) {
        var d = domain?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(d) ? null : d;
    }

    public static DomainKind ParseKind(string? kind) =>
        Enum.TryParse<DomainKind>(kind, ignoreCase: true, out var k) ? k : DomainKind.Managed;

    /// <summary>
    /// Refuses a non-system realm's Watchtower route on the hostname the configured
    /// <c>Auth:Host</c> names, or <see langword="null"/> when there is no collision (ADR-0023).
    /// </summary>
    /// <remarks>
    /// <c>Auth:Host</c> is the operator realm's <em>fallback</em> login host, read while that realm has
    /// no login route of its own. A customer realm serving Watchtower on the same hostname would send
    /// operator-realm visitors to a login page that cannot admit them, and give both populations the
    /// same token issuer — which <see cref="Services.RealmResolver.IssuersAsync"/> can then only resolve
    /// by dropping one. The check is symmetric with the one in <c>system.updateAuthConfig</c>: whichever
    /// of the two is written second is refused, so neither order can reach the collision.
    /// </remarks>
    public static AppError? CheckAuthHostCollision(string domain, Realm realm, string? configuredAuthHost) {
        ArgumentNullException.ThrowIfNull(realm);
        if (realm.IsSystem) return null;

        var authHost = Services.RouteAccessPolicy.NormalizeForwardedHost(configuredAuthHost);
        if (authHost is null || !string.Equals(authHost, domain, StringComparison.Ordinal)) return null;

        return AppError.Validation(
            $"'{domain}' is the configured Watchtower:Auth:Host — the operator realm's fallback login " +
            $"host — so realm '{realm.Slug}' cannot serve Watchtower on it: operator visitors would be " +
            "sent to this realm's login page and both populations would mint under one token issuer. " +
            "Clear Auth:Host (Settings → Authentication) or choose another hostname.");
    }

    /// <summary>
    /// Reads the wire form of <see cref="RouteTarget"/>, defaulting to <see cref="RouteTarget.Service"/>
    /// for a blank or absent value: a client that predates ADR-0023 sends nothing and means the only kind
    /// of route that existed then. An unrecognised value is refused rather than defaulted — creating a
    /// forwarded route because a typo did not parse is the wrong direction to fail in.
    /// </summary>
    public static bool TryParseTarget(string? target, out RouteTarget parsed) {
        if (string.IsNullOrWhiteSpace(target)) {
            parsed = RouteTarget.Service;
            return true;
        }
        return Enum.TryParse(target.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }
}
