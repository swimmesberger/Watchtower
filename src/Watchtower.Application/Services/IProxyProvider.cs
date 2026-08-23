namespace Watchtower.Application.Services;

/// <summary>
/// A reverse-proxy backend that projects the <c>routes</c> table onto real ingress (ADR-0015). Three
/// implementations exist: <see cref="CaddyManager"/> (a sibling Caddy container on host ports 80/443,
/// automatic TLS), <see cref="CloudflareTunnelProvider"/> (a cloudflared tunnel configured via the
/// Cloudflare API) and <see cref="Yarp.YarpProxyProvider"/> — in-process, Watchtower binds 80/443
/// itself, per ADR-0022.
/// Consumers — the deploy queue, tenant teardown, and the <c>proxy.*</c>/<c>realms.*</c> handlers —
/// inject this interface; <see cref="ProxyProviderRouter"/> resolves the active provider per call from
/// <c>Proxy:Provider</c>, so the backend is runtime-switchable exactly like the metrics backend
/// (ADR-0007). Each provider self-gates: every method no-ops unless the provider is the selected one
/// and the proxy is enabled, mirroring how the pre-abstraction <c>CaddyManager</c> behaved.
/// </summary>
public interface IProxyProvider {
    /// <summary>True when the proxy is enabled and this provider is the selected backend.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Projects the current route table into the proxy's configuration (Caddyfile push, or tunnel
    /// ingress rules + DNS). Best-effort: never throws, so a proxy hiccup can't fail the route CRUD
    /// or deploy that triggered it.
    /// </summary>
    Task ApplyAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes what the provider holds for a hostname that is no longer a route — the route-delete
    /// "remove from the provider too" path. Cloudflare: the tunnel ingress rule and the CNAME Watchtower
    /// pointed at the tunnel; Caddy: nothing, its configuration is regenerated from the table. Unlike
    /// <see cref="ApplyAsync"/> this THROWS on failure — the caller asked for a specific external
    /// change and must be told when it did not happen. <paramref name="actor"/> is recorded on the
    /// audit rows.
    /// </summary>
    Task ForgetDomainAsync(string domain, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Joins the routed service container(s) of a stack to its ingress network under the stable
    /// alias, so the proxy can reach them. Best-effort: never throws.
    /// </summary>
    Task ConnectStackAsync(int stackId, CancellationToken ct = default);

    /// <summary>True when the provider's data plane (its managed container) reports running.</summary>
    Task<bool> IsRunningAsync(CancellationToken ct = default);
}
