using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
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
    /// The refusals for the ports of <paramref name="ports"/> that another container on this host already
    /// publishes, keyed by port — empty when none of them is taken. The friendly half of a bind that would
    /// otherwise fail somewhere nobody is looking: a port route's listener is on Watchtower's own container,
    /// so a stack that publishes the same host port takes it away, and the recreate that would publish it
    /// fails to start, rolls back, and leaves the route reporting "host port not published" with nothing
    /// naming the container that holds it.
    /// </summary>
    /// <param name="selfContainerId">
    /// Watchtower's own container, which is excluded — it is where the listener lives, and a port it
    /// already publishes is the state this whole feature is trying to reach. Null falls back to
    /// <c>HOSTNAME</c>, the same reading <c>SelfUpdateService.DetectSelfAsync</c> starts from; a prefix
    /// match either way, since <c>HOSTNAME</c> is the short id and the daemon reports the long one. With
    /// no answer at all nothing is excluded, which is the safe direction: the worst case is a refusal
    /// naming Watchtower's own container, not a collision that goes unmentioned.
    /// </param>
    /// <remarks>
    /// <b>Fail-open by design.</b> A Docker call that throws — no socket, a bare-process install, a daemon
    /// that is briefly unreachable — logs one warning and refuses nothing. This is a convenience against a
    /// footgun, not a security boundary: nothing here decides what is served, and being unable to ask the
    /// daemon must not be what stops an operator creating a route.
    /// <para>
    /// Containers in <em>any</em> state count, the way <c>networks.ports</c> deliberately reads them: a
    /// stopped stack whose desired state is running comes back, taking the port with it. Only the TCP half
    /// counts — a <c>9001/udp</c> binding is not in the way of an HTTPS listener — and a type Docker left
    /// off is TCP, which is what the daemon assumes for a bare port number too.
    /// </para>
    /// </remarks>
    public static async ValueTask<IReadOnlyDictionary<int, string>> PublishedByOtherContainersAsync(
        DockerEngineClient docker,
        IReadOnlyCollection<int> ports,
        string? selfContainerId,
        ILogger? logger,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(ports);
        if (ports.Count == 0) return ReadOnlyDictionary<int, string>.Empty;

        IReadOnlyList<DockerContainerInfo> containers;
        try {
            containers = await docker.ListAllContainersAsync(ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger?.LogWarning(
                ex,
                "Could not ask Docker which containers publish host port(s) {Ports}; the port-route "
                + "collision check is skipped.",
                string.Join(", ", ports));
            return ReadOnlyDictionary<int, string>.Empty;
        }

        var self = string.IsNullOrWhiteSpace(selfContainerId)
            ? Environment.GetEnvironmentVariable("HOSTNAME")
            : selfContainerId;
        var wanted = new HashSet<int>(ports);
        var blocked = new Dictionary<int, string>();

        foreach (var container in containers) {
            if (IsSameContainer(container.Id, self)) continue;
            foreach (var port in container.Ports) {
                if (port.PublicPort is not { } published || !wanted.Contains(published)) continue;
                if (!IsTcpBinding(port.Type)) continue;
                blocked.TryAdd(published, PortHeldBy(published, container));
            }
        }
        return blocked;
    }

    /// <summary>
    /// <inheritdoc cref="PublishedByOtherContainersAsync" path="/summary"/> The one-port form the route
    /// handlers use, over the same reading.
    /// </summary>
    public static async ValueTask<string?> PublishedByAnotherContainerAsync(
        DockerEngineClient docker,
        int listenPort,
        string? selfContainerId,
        ILogger? logger,
        CancellationToken ct) {
        var blocked = await PublishedByOtherContainersAsync(
            docker, [listenPort], selfContainerId, logger, ct);
        return blocked.TryGetValue(listenPort, out var refusal) ? refusal : null;
    }

    /// <summary>The refusal itself: what holds the port, and the two ways out of it.</summary>
    private static string PortHeldBy(int port, DockerContainerInfo container) {
        var name = container.Names.Length > 0 && !string.IsNullOrWhiteSpace(container.Names[0])
            ? container.Names[0].TrimStart('/')
            : container.Id;
        var project = container.Labels.TryGetValue(ComposeProjectLabel, out var p) && !string.IsNullOrWhiteSpace(p)
            ? p
            : null;
        var service = container.Labels.TryGetValue(ComposeServiceLabel, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;
        var labels = (project, service) switch {
            (not null, not null) => $" (stack {project}, service {service})",
            (not null, null) => $" (stack {project})",
            (null, not null) => $" (service {service})",
            _ => "",
        };
        return $"Host port {port} is already published by container {name}{labels}. A port route needs "
            + "that port for Watchtower's own listener — remove that ports: entry from the stack or "
            + "choose another port.";
    }

    /// <summary>Whether a listed container is the one this process runs in; see the parameter's note.</summary>
    private static bool IsSameContainer(string id, string? self) =>
        !string.IsNullOrWhiteSpace(id)
        && !string.IsNullOrWhiteSpace(self)
        && (id.StartsWith(self, StringComparison.OrdinalIgnoreCase)
            || self.StartsWith(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A binding with no protocol at all is TCP, the same way a bare port number is.</summary>
    private static bool IsTcpBinding(string? type) =>
        string.IsNullOrEmpty(type) || string.Equals(type, "tcp", StringComparison.OrdinalIgnoreCase);

    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    /// <summary>
    /// Whether the deployment names at least one LAN address the internal CA can issue a leaf for. Junk
    /// that does not parse counts as none: <c>proxy.updateConfig</c> refuses such a value, so the only way
    /// to hold one is an environment pin, and issuance would fail on it the same way.
    /// </summary>
    public static bool HasLanNames(YarpProxyOptions yarp) {
        ArgumentNullException.ThrowIfNull(yarp);
        return InternalCaNames.TryParseLanNames(yarp.LanNames, out var dnsNames, out var ips, out _)
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
