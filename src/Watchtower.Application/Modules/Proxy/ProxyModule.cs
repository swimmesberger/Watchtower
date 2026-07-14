using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>
/// Reverse-proxy module: manages public <c>Route</c>s (domain → service) served by the built-in Caddy
/// proxy with automatic TLS. Handlers are exposed as <c>proxy.*</c> JSON-RPC methods.
/// </summary>
[AppModule("Proxy")]
public static partial class ProxyModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => ProxyJsonContext.Default;
}
