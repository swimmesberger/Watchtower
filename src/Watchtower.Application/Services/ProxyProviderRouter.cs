using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// The registered <see cref="IProxyProvider"/>: resolves the selected backend per call from
/// <c>Proxy:Provider</c> (via <see cref="IOptionsMonitor{WatchtowerOptions}"/>, so a runtime settings
/// change re-routes the very next call — same pattern as <c>MetricsSourceRouter</c>, ADR-0007/0015).
/// Providers are resolved from the container rather than captured, so a test that substitutes
/// <see cref="CaddyManager"/> (e.g. <c>RecordingCaddyManager</c>) is still what the router serves.
/// </summary>
public sealed class ProxyProviderRouter(IServiceProvider services, IOptionsMonitor<WatchtowerOptions> options)
    : IProxyProvider {
    private IProxyProvider Current => options.CurrentValue.Proxy.ResolveProvider() switch {
        ProxyProviderKind.Cloudflare => services.GetRequiredService<CloudflareTunnelProvider>(),
        _ => services.GetRequiredService<CaddyManager>(),
    };

    public bool Enabled => Current.Enabled;

    public Task ApplyAsync(CancellationToken ct = default) => Current.ApplyAsync(ct);

    public Task ForgetDomainAsync(string domain, string? actor, CancellationToken ct = default) =>
        Current.ForgetDomainAsync(domain, actor, ct);

    public Task ConnectStackAsync(int stackId, CancellationToken ct = default) => Current.ConnectStackAsync(stackId, ct);

    public Task<bool> IsRunningAsync(CancellationToken ct = default) => Current.IsRunningAsync(ct);
}
