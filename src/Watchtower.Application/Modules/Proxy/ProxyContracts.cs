using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.InternalCa;

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
/// its own instead.
/// </param>
/// <param name="Binding">
/// <c>domain</c> or <c>port</c> (ADR-0033) — how this route is addressed. A <c>port</c> route carries a
/// <paramref name="ListenPort"/> and no <paramref name="Domain"/>; a <c>domain</c> route the other way
/// round. Immutable after creation, like <paramref name="Target"/>.
/// </param>
/// <param name="ListenPort">
/// The host port a <c>port</c> route's own TLS listener answers on; null on a <c>domain</c> route. The
/// address a client types is that port together with one of the deployment's LAN names, which is why the
/// client renders it from this and the configured names rather than storing a URL.
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
    bool IsLoginRoute,
    string Binding,
    int? ListenPort);

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
            isLoginRoute,
            r.Binding.ToString().ToLowerInvariant(),
            r.ListenPort);
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

    /// <summary>
    /// Reads the wire form of <see cref="RouteBinding"/>, defaulting to <see cref="RouteBinding.Domain"/>
    /// for a blank or absent value — the only kind of route that existed before ADR-0033, and what every
    /// client that says nothing means. An unrecognised value is refused rather than defaulted, for the
    /// same reason <see cref="TryParseTarget"/> refuses one: a typo must not quietly create a route
    /// addressed differently than the caller asked for.
    /// </summary>
    public static bool TryParseBinding(string? binding, out RouteBinding parsed) {
        if (string.IsNullOrWhiteSpace(binding)) {
            parsed = RouteBinding.Domain;
            return true;
        }
        return Enum.TryParse(binding.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }
}

/// <summary>
/// What a <see cref="RouteBinding.Port"/> route's listen port has to satisfy before the row is written
/// (ADR-0033), shared by <c>proxy.createRoute</c> and <c>proxy.updateRoute</c> so a port a create accepted
/// is never one an edit that changed nothing else would refuse.
/// </summary>
/// <remarks>
/// Every rule here is about a listener actually coming up on the port. The ones about what a port route
/// <em>is</em> — a public, TLS, service-target row with no hostname — are the check constraint's, not
/// these, because those are invariants the request path relies on rather than advice at the boundary.
/// </remarks>
internal static class PortRouteRules {
    /// <summary>
    /// Checks a listen port against everything else this process binds, returning the operator-facing
    /// message or null. Unlike the ingress ports, <c>0</c> is not an answer here: a port route <em>is</em>
    /// its listener, so turning it off would leave a row nothing serves.
    /// </summary>
    public static string? ValidateListenPort(int listenPort, int? managementPort, YarpProxyOptions yarp) {
        ArgumentNullException.ThrowIfNull(yarp);
        if (listenPort is < 1 or > 65535)
            return "The listen port must be between 1 and 65535.";
        if (managementPort is { } management && listenPort == management) {
            return $"Port {listenPort} is the management port — that is the listener Watchtower's own UI "
                + "and API are served on.";
        }
        if (yarp.HttpPort != 0 && listenPort == yarp.HttpPort) {
            return $"Port {listenPort} is the in-process proxy's HTTP ingress port, where domain routes "
                + "are served. Choose a port of its own.";
        }
        if (yarp.HttpsPort != 0 && listenPort == yarp.HttpsPort) {
            return $"Port {listenPort} is the in-process proxy's HTTPS ingress port, where domain routes "
                + "are served. Choose a port of its own.";
        }
        return null;
    }

    /// <summary>
    /// The refusal naming the route that already holds <paramref name="listenPort"/>, or null when none
    /// does. The friendly half of the filtered unique index on <c>listen_port</c> — which is what still
    /// decides the question under a race, or between two instances on one database.
    /// </summary>
    /// <param name="exceptRouteId">The route being edited, which does not collide with itself.</param>
    public static async ValueTask<string?> TakenByAsync(
        WatchtowerDbContext db, int listenPort, int? exceptRouteId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        var taken = await db.Routes.AsNoTracking()
            .Where(r => r.ListenPort == listenPort && (exceptRouteId == null || r.Id != exceptRouteId))
            .Select(r => new { r.Id, r.ServiceName })
            .FirstOrDefaultAsync(ct);
        return taken is null
            ? null
            : $"Port {listenPort} is already served by route {taken.Id} ({taken.ServiceName}). "
              + "Two routes cannot share one listener.";
    }

    /// <summary>
    /// Whether the deployment names at least one LAN address the internal CA can issue a leaf for. Junk
    /// that does not parse counts as none: <c>proxy.updateConfig</c> refuses such a value, so the only way
    /// to hold one is an environment pin, and issuance would fail on it the same way.
    /// </summary>
    public static bool HasLanNames(PortRouteOptions portRoutes) {
        ArgumentNullException.ThrowIfNull(portRoutes);
        return InternalCaNames.TryParseLanNames(portRoutes.LanNames, out var dnsNames, out var ips, out _)
            && (dnsNames.Count > 0 || ips.Count > 0);
    }

    /// <summary>
    /// What an operator is told to do before a port route can be created at all: without a LAN name the
    /// internal CA has nothing to issue for, so the route would come up permanently untrusted.
    /// </summary>
    public const string NoLanNames =
        "Set the LAN names in Settings → Reverse proxy first — the certificate has to carry the name or "
        + "IP you will type in the browser.";
}
