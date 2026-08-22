using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Api.Proxy;

/// <summary>
/// Records what the host actually bound into <see cref="YarpListenerState"/> once the server is up —
/// ADR-0017 (forthcoming). Everything the in-process proxy reports about itself ("running", "enabled but
/// 443 never came up") comes from here, because there is no container to inspect.
/// </summary>
/// <remarks>
/// Hooked to <c>ApplicationStarted</c> rather than written as an <c>IHostedService</c> because the
/// addresses only exist after the server has bound, which is after every hosted service has started.
/// </remarks>
internal static class ProxyListenerStateInitializer {
    public static void Register(WebApplication app) =>
        app.Lifetime.ApplicationStarted.Register(() => {
            var state = app.Services.GetRequiredService<YarpListenerState>();
            // Under TestServer there is no address feature at all, and a host that failed to expose one
            // is not a reason to bring the process down — the state simply stays "nothing bound".
            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses;
            Apply(state, addresses, app.Logger);
        });

    /// <summary>The pure half: turn the bound addresses into the two facts the proxy reports.</summary>
    internal static void Apply(YarpListenerState state, ICollection<string>? addresses, ILogger logger) {
        if (addresses is null || addresses.Count == 0) return;

        state.HttpsBound = addresses.Any(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        state.LocalHttpAddress = addresses
            .Where(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            .Select(Dialable)
            .FirstOrDefault();

        logger.LogInformation(
            "In-process proxy listeners: HTTPS {HttpsBound}, local HTTP {LocalHttpAddress}.",
            state.HttpsBound ? "bound" : "not bound", state.LocalHttpAddress ?? "none");
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
