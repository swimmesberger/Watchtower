using System.Globalization;
using Microsoft.Extensions.Primitives;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Api.Proxy;

/// <summary>
/// Keeps <see cref="YarpListenerState"/> in step with the projected Kestrel section — ADR-0022.
/// Everything the in-process proxy reports about itself ("running", "TLS ingress is off") comes from
/// here, because there is no container to inspect — and so does the one fact the dispatcher needs per
/// request: which local ports are <em>ingress</em> rather than management.
/// </summary>
/// <remarks>
/// <para>
/// The source of truth is <see cref="ProxyIngressKestrelConfiguration"/>'s projection rather than the
/// server's bound addresses, because the endpoints are no longer a startup-only fact: they come and go
/// with the reverse-proxy settings, and there is no "the addresses changed" event to hang the update on.
/// Reading the same section Kestrel reads also means the two can only ever disagree about a bind that
/// failed — which Kestrel logs, and which is the safe direction to be wrong in (see
/// <see cref="YarpListenerState"/>).
/// </para>
/// <para>
/// This replaces the previous <c>ApplicationStarted</c> narrowing. The seed-then-narrow shape existed to
/// close a window — Kestrel accepts connections on the ingress ports while hosted services are still
/// starting, and an empty <c>IngressPorts</c> there is the dispatcher's fall-through rule in force on a
/// public port. Deriving from configuration closes it outright: the facts are in place before the host is
/// built, and they never widen afterwards.
/// </para>
/// </remarks>
internal static class ProxyListenerStateInitializer {
    public static void Register(WebApplication app, IConfiguration kestrelSection) {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(kestrelSection);

        var state = app.Services.GetRequiredService<YarpListenerState>();
        // The projection is built before the host is, so a port it refused up to now is still held
        // unlogged. Attaching the logger here flushes it into the ordinary log instead of stderr.
        app.Services.GetRequiredService<ProxyIngressWarnings>().UseLogger(app.Logger);
        // ASPNETCORE_URLS / launchSettings / UseUrls — the shape of a host configured without named
        // endpoints, which is every development and test run. Read once: it cannot change at runtime.
        var hostingUrls = app.Configuration[WebHostDefaults.ServerUrlsKey];

        Apply(state, kestrelSection, hostingUrls, app.Logger);
        ChangeToken.OnChange(
            kestrelSection.GetReloadToken,
            () => Apply(state, kestrelSection, hostingUrls, app.Logger));
    }

    /// <summary>Publishes the derived reading and says so in the log.</summary>
    internal static void Apply(
        YarpListenerState state, IConfiguration kestrelSection, string? hostingUrls, ILogger logger) {
        var snapshot = Derive(kestrelSection, hostingUrls);
        state.Publish(snapshot);

        logger.LogInformation(
            "In-process proxy listeners: HTTPS {HttpsBound}, local HTTP {LocalHttpAddress}, "
            + "management port {ManagementPort}, ingress ports {IngressPorts}, port routes {PortRoutePorts}.",
            snapshot.HttpsBound ? "configured" : "off",
            snapshot.LocalHttpAddress ?? "none",
            snapshot.ManagementPort?.ToString(CultureInfo.InvariantCulture) ?? "none",
            snapshot.IngressPorts.Count == 0 ? "none" : string.Join(", ", snapshot.IngressPorts.Order()),
            snapshot.PortRoutePorts.Count == 0 ? "none" : string.Join(", ", snapshot.PortRoutePorts.Order()));
    }

    /// <summary>
    /// The pure half: what the projected section says the listeners are. <paramref name="hostingUrls"/> is
    /// the fallback for the management port only — a host that binds through <c>ASPNETCORE_URLS</c> has no
    /// named endpoints at all, and its single listener is the management plane by definition.
    /// </summary>
    internal static YarpListenerSnapshot Derive(IConfiguration kestrelSection, string? hostingUrls) {
        var httpPort = ListenerUrl.PortOf(kestrelSection[$"Endpoints:{ProxyHttpsEndpoint.HttpEndpointName}:Url"]);
        var httpsPort = ListenerUrl.PortOf(kestrelSection[$"Endpoints:{ProxyHttpsEndpoint.EndpointName}:Url"]);
        var managementPort =
            ListenerUrl.PortOf(
                kestrelSection[$"Endpoints:{ProxyIngressKestrelConfiguration.ManagementEndpointName}:Url"])
            ?? FirstHttpPort(hostingUrls);

        // The ACME self-check dials this, and what it has to dial is the listener the CA reaches: the
        // ingress HTTP endpoint where there is one (the operator publishes 80 onto it), and the management
        // endpoint otherwise, which is the single-listener shape this had before the endpoints were split.
        var dialablePort = httpPort ?? managementPort;

        // The port-bound routes' listeners (ADR-0033). Read back out of the projected section rather than
        // out of the setting the projection derived them from, so a port the projection dropped — one that
        // collided with the management or ingress ports — is absent here too: what this publishes has to be
        // the endpoints Kestrel was actually asked for.
        var portRoutePorts = PortRouteEndpoints(kestrelSection);

        return new YarpListenerSnapshot {
            HttpsBound = httpsPort is not null,
            // Port routes are ingress like the two named endpoints: their listeners are published to the
            // network and must never fall through to the management plane.
            IngressPorts = new[] { httpPort, httpsPort }.OfType<int>().Concat(portRoutePorts).ToHashSet(),
            PortRoutePorts = portRoutePorts.ToHashSet(),
            ManagementPort = managementPort,
            LocalHttpAddress = dialablePort is { } port
                ? string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}")
                : null,
        };
    }

    /// <summary>
    /// The ports of the <c>ProxyPort{n}</c> endpoints in the projected section. The port is taken from the
    /// endpoint's URL rather than from its name, so what is published is the port Kestrel binds even if the
    /// two could ever disagree.
    /// </summary>
    private static List<int> PortRouteEndpoints(IConfiguration kestrelSection) {
        var ports = new List<int>();
        foreach (var endpoint in kestrelSection.GetSection("Endpoints").GetChildren()) {
            if (!PortRouteListeners.IsPortEndpointName(endpoint.Key)) continue;
            if (ListenerUrl.PortOf(endpoint["Url"]) is { } port) ports.Add(port);
        }
        return ports;
    }

    /// <summary>The port of the first plain-HTTP URL in a semicolon-separated hosting URL list.</summary>
    private static int? FirstHttpPort(string? hostingUrls) => hostingUrls?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        .Select(ListenerUrl.PortOf)
        .FirstOrDefault(port => port is not null);

}
