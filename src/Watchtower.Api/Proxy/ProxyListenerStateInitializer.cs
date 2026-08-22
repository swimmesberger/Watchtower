using System.Globalization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Api.Proxy;

/// <summary>
/// Records what the host actually bound into <see cref="YarpListenerState"/> once the server is up —
/// ADR-0020. Everything the in-process proxy reports about itself ("running", "enabled but
/// 443 never came up") comes from here, because there is no container to inspect — and so does the one
/// fact the dispatcher needs per request: which local ports are <em>ingress</em> rather than management.
/// </summary>
/// <remarks>
/// Hooked to <c>ApplicationStarted</c> rather than written as an <c>IHostedService</c> because the
/// addresses only exist after the server has bound, which is after every hosted service has started.
/// </remarks>
internal static class ProxyListenerStateInitializer {
    /// <summary>The management endpoint: Watchtower's own UI and API. Never ingress.</summary>
    private const string ManagementEndpoint = "Http";

    public static void Register(WebApplication app) {
        var state = app.Services.GetRequiredService<YarpListenerState>();

        // Seeded here, before the callback is even hooked, because ApplicationStarted fires only after
        // every hosted service has started — and Kestrel is accepting connections on the ingress ports the
        // whole time those run. An empty IngressPorts during that window is not a missing diagnostic, it is
        // the dispatcher's fall-through rule in force on ports published to the internet: exactly the hole
        // the split exists to close, reopened on every start for as long as the slowest hosted service
        // takes. The configured endpoints are known now and are the honest guess; the callback below
        // narrows them to what really bound.
        Seed(state, app.Configuration);

        app.Lifetime.ApplicationStarted.Register(() => {
            // Under TestServer there is no address feature at all, and a host that failed to expose one
            // is not a reason to bring the process down — the state simply keeps the seed.
            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses;
            Apply(state, addresses, app.Configuration, app.Logger);
        });
    }

    /// <summary>
    /// The ports the configuration names, recorded before anything binds. Deliberately the widest of the
    /// two readings: a configured endpoint that then fails to bind costs nothing here (no request can
    /// arrive on a port nobody is listening to), whereas a bound endpoint not yet recorded is a hole.
    /// </summary>
    internal static void Seed(YarpListenerState state, IConfiguration configuration) =>
        Record(
            state,
            Set(ConfiguredPort(configuration, ProxyHttpsEndpoint.HttpEndpointName),
                ConfiguredPort(configuration, ProxyHttpsEndpoint.EndpointName)),
            ConfiguredPort(configuration, ManagementEndpoint));

    /// <summary>The pure half: turn the bound addresses into the facts the proxy reports about itself.</summary>
    /// <remarks>
    /// The configured URLs are what tells an ingress port from the management one — a bound address is just
    /// a port, and "which endpoint is this?" is a question only <c>Kestrel:Endpoints:*</c> can answer. They
    /// are also the fallback when there is no address feature to consult: an operator who published
    /// <c>80:8081</c> is owed the ingress rule whether or not the server chose to describe itself.
    /// </remarks>
    internal static void Apply(
        YarpListenerState state, ICollection<string>? addresses, IConfiguration configuration, ILogger logger) {
        var proxyHttpPort = ConfiguredPort(configuration, ProxyHttpsEndpoint.HttpEndpointName);
        var proxyHttpsPort = ConfiguredPort(configuration, ProxyHttpsEndpoint.EndpointName);
        var managementPort = ConfiguredPort(configuration, ManagementEndpoint);

        if (addresses is null || addresses.Count == 0) {
            // No address feature — TestServer, or a server that exposes none. The configuration is the only
            // evidence there is, and where it names nothing the state is left exactly as it was: an empty
            // derivation is not a finding, and overwriting a value someone else set with it would be.
            Record(state, Set(proxyHttpPort, proxyHttpsPort), managementPort);
            return;
        }

        var boundPorts = addresses.Select(PortOf).OfType<int>().ToList();
        var ingressPorts = boundPorts.Where(p => p == proxyHttpPort || p == proxyHttpsPort).ToHashSet();

        state.HttpsBound = addresses.Any(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        Record(
            state,
            ingressPorts,
            // The configured management endpoint where it is one of the ports actually bound; otherwise
            // whatever was bound that is not ingress, which is the shape of a host configured through
            // ASPNETCORE_URLS instead of named endpoints.
            managementPort is { } configured && boundPorts.Contains(configured)
                ? configured
                : boundPorts.Where(p => !ingressPorts.Contains(p)).Select(p => (int?)p).FirstOrDefault());

        // The ACME self-check dials this, and what it has to dial is the listener the CA reaches: the
        // ingress HTTP endpoint where one is bound (the operator publishes 80 onto it), and the management
        // endpoint otherwise, which is the single-listener shape this had before the split.
        var httpAddresses = addresses
            .Where(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ingressHttp = proxyHttpPort is { } httpPort
            ? httpAddresses.FirstOrDefault(a => PortOf(a) == httpPort)
            : null;
        state.LocalHttpAddress = (ingressHttp ?? httpAddresses.FirstOrDefault()) is { } dialable
            ? Dialable(dialable)
            : null;

        logger.LogInformation(
            "In-process proxy listeners: HTTPS {HttpsBound}, local HTTP {LocalHttpAddress}, "
            + "management port {ManagementPort}, ingress ports {IngressPorts}.",
            state.HttpsBound ? "bound" : "not bound",
            state.LocalHttpAddress ?? "none",
            state.ManagementPort?.ToString(CultureInfo.InvariantCulture) ?? "none",
            state.IngressPorts.Count == 0
                ? "none"
                : string.Join(", ", state.IngressPorts.Order()));
    }

    /// <summary>
    /// Writes the port facts, skipping anything that came out empty. Nothing derived is not the same as
    /// "there is nothing": an empty derivation leaves the earlier seed — or whatever a test host put there
    /// — standing, and a non-empty one narrows it. Widening never happens by accident, because the only
    /// two writers are the seed and the bind.
    /// </summary>
    private static void Record(YarpListenerState state, HashSet<int> ingress, int? management) {
        if (ingress.Count > 0) state.IngressPorts = ingress;
        if (management is not null) state.ManagementPort = management;
    }

    private static HashSet<int> Set(params int?[] ports) => [.. ports.OfType<int>()];

    /// <summary>The port an endpoint is configured on, or <see langword="null"/> when it is not configured.</summary>
    private static int? ConfiguredPort(IConfiguration configuration, string endpointName) =>
        PortOf(configuration[$"Kestrel:Endpoints:{endpointName}:Url"]);

    /// <summary>
    /// The port in a Kestrel URL, whether it is a bound address (<c>http://[::]:8080</c>) or a configured
    /// wildcard (<c>http://+:8080</c>) — neither of which <see cref="Uri"/> will parse. A URL that names no
    /// port falls back to its scheme's default, because that is the port it will bind.
    /// </summary>
    private static int? PortOf(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return null;
        var scheme = url[..schemeEnd];
        var rest = url[(schemeEnd + 3)..];
        var end = rest.IndexOf('/');
        var authority = end < 0 ? rest : rest[..end];

        // IPv6 literals keep their brackets, so the port separator is the last colon outside them.
        var colon = authority.LastIndexOf(':');
        var closing = authority.LastIndexOf(']');
        if (colon <= closing)
            return scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

        return int.TryParse(authority[(colon + 1)..], out var port) && port > 0 ? port : null;
    }

    /// <summary>
    /// Rewrites a wildcard bind into something this process can actually dial itself — the ACME HTTP-01
    /// self-check connects to its own listener, and <c>http://+:8080</c> is not a URL a client can use.
    /// A concrete host (<c>localhost</c>, a specific address) is left exactly as it is.
    /// </summary>
    private static string Dialable(string address) {
        const string Scheme = "http://";
        var rest = address[Scheme.Length..];
        var end = rest.IndexOf('/');
        var authority = end < 0 ? rest : rest[..end];
        var tail = end < 0 ? "" : rest[end..];

        // The port (and only the port) survives the rewrite; IPv6 literals keep their brackets, so the
        // separator is the last colon outside them.
        var colon = authority.LastIndexOf(':');
        var closing = authority.LastIndexOf(']');
        var host = colon > closing ? authority[..colon] : authority;
        var port = colon > closing ? authority[colon..] : "";

        return host is "+" or "*" or "0.0.0.0" or "[::]" or "[::0]"
            ? $"{Scheme}127.0.0.1{port}{tail}"
            : address;
    }
}
