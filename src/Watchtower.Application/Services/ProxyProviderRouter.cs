using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Services;

/// <summary>
/// The registered <see cref="IProxyProvider"/>: resolves the selected backend per call from
/// <c>Proxy:Provider</c> (via <see cref="IOptionsMonitor{WatchtowerOptions}"/>, so a runtime settings
/// change re-routes the very next call — same pattern as <c>MetricsSourceRouter</c>, ADR-0007/0015).
/// Providers are resolved from the container rather than captured, so a substitute registered for one of
/// them is still what the router serves. Tests generally do not go that way, though: they replace
/// <see cref="IProxyProvider"/> itself with a recording double, which is the seam every consumer injects
/// and the one that does not move when the default provider does (ADR-0022 changed it to <c>yarp</c>).
/// </summary>
public sealed class ProxyProviderRouter(IServiceProvider services, IOptionsMonitor<WatchtowerOptions> options)
    : IProxyProvider {
    private IProxyProvider Current => options.CurrentValue.Proxy.ResolveProvider() switch {
        ProxyProviderKind.Cloudflare => services.GetRequiredService<CloudflareTunnelProvider>(),
        ProxyProviderKind.Yarp => services.GetRequiredService<YarpProxyProvider>(),
        _ => services.GetRequiredService<CaddyManager>(),
    };

    public bool Enabled => Current.Enabled;

    public Task ApplyAsync(CancellationToken ct = default) => Current.ApplyAsync(ct);

    public Task ForgetDomainAsync(string domain, string? actor, CancellationToken ct = default) =>
        Current.ForgetDomainAsync(domain, actor, ct);

    public Task ConnectStackAsync(int stackId, CancellationToken ct = default) => Current.ConnectStackAsync(stackId, ct);

    public Task<bool> IsRunningAsync(CancellationToken ct = default) => Current.IsRunningAsync(ct);
}
