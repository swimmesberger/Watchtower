namespace Watchtower.Api.Proxy;

/// <summary>
/// Host-side registration for the in-process proxy's request path — ADR-0020. The control
/// plane (the route table, the provider, the challenge store) is registered by
/// <c>AddWatchtowerServices</c> in the Application layer; only these two touch YARP, which is why they
/// live here and the Application project stays proxy-agnostic.
/// </summary>
public static class ProxyForwardingServiceCollectionExtensions {
    /// <summary>
    /// Adds YARP's direct forwarder and the singleton client it forwards on. Unconditional, like the
    /// middleware: the provider is switchable at runtime and the DI container is built once.
    /// </summary>
    /// <remarks>
    /// <c>AddHttpForwarder</c> registers <c>IHttpForwarder</c> alone — not YARP's configuration,
    /// route-matching or endpoint machinery. Watchtower already owns the route table and dispatches on
    /// <c>Host</c> itself, so the only part of YARP in play is the one that is genuinely hard: the
    /// request/response copy, with its trailers, upgrades and streaming.
    /// </remarks>
    public static IServiceCollection AddWatchtowerProxyForwarding(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpForwarder();
        services.AddSingleton<ProxyForwardHttpClient>();
        return services;
    }
}
