using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services.PortRoutes;
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
/// <remarks>
/// It is also where the cross-instance change signal is raised (ADR-0024 decision 6). Every route and
/// realm write handler ends by calling <see cref="ApplyAsync"/> through this interface, so bumping the
/// version here covers all of them — and, more to the point, covers the next one somebody writes. The
/// alternative was a line in each handler, which is the shape of rule a new handler can be added without.
/// <para>
/// The watchers on the other instances call <see cref="YarpProxyProvider.ApplyAsync"/> and
/// <see cref="PortRoutePlane.ApplyAsync"/> directly rather than going through this router, which is what
/// stops a signal from feeding itself: a re-projection triggered by a notification must not publish
/// another notification.
/// </para>
/// <para>
/// <b>Two planes, not two providers</b> (ADR-0033 addendum). Alongside whichever provider is selected
/// there is always <see cref="PortRoutePlane"/>, which serves the port-bound routes on Watchtower's own
/// container. It is not an <see cref="IProxyProvider"/> and is not selected by <c>Proxy:Provider</c> —
/// there is nothing to select between — so the router does not route to it, it drives it as well. Only
/// the two operations that mean "the route table changed" and "this stack's containers moved" fan out;
/// <see cref="Enabled"/>, <see cref="IsRunningAsync"/> and <see cref="ForgetDomainAsync"/> are
/// statements about the domain provider and stay with it.
/// </para>
/// </remarks>
public sealed class ProxyProviderRouter(
    IServiceProvider services, ProxyChangeSignal signal, IOptionsMonitor<WatchtowerOptions> options)
    : IProxyProvider {
    private IProxyProvider Current => options.CurrentValue.Proxy.ResolveProvider() switch {
        ProxyProviderKind.Cloudflare => services.GetRequiredService<CloudflareTunnelProvider>(),
        ProxyProviderKind.Yarp => services.GetRequiredService<YarpProxyProvider>(),
        _ => services.GetRequiredService<CaddyManager>(),
    };

    /// <summary>Resolved per call like the providers, so a test substitute is what the router drives.</summary>
    private PortRoutePlane PortRoutes => services.GetRequiredService<PortRoutePlane>();

    public bool Enabled => Current.Enabled;

    /// <summary>
    /// Re-projects locally — the selected provider's domain routes and the port routes, which are
    /// nobody's provider — then tells the other instances to do the same. In that order: the instance
    /// the operator is talking to should be correct before it answers them, and the signal is what makes
    /// the rest correct a moment later.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default) {
        await Current.ApplyAsync(ct);
        await PortRoutes.ApplyAsync(ct);
        await signal.BumpAsync("route or realm change", ct);
    }

    public Task ForgetDomainAsync(string domain, string? actor, CancellationToken ct = default) =>
        // No bump of its own: this deletes the certificate, and the store raises the signal for that —
        // then the caller's ApplyAsync above raises it for the route change. Not fanned out to the port
        // plane: a port route has no domain, and its certificate is the internal CA's shared leaf.
        Current.ForgetDomainAsync(domain, actor, ct);

    public async Task ConnectStackAsync(int stackId, CancellationToken ct = default) {
        await Current.ConnectStackAsync(stackId, ct);
        await PortRoutes.ConnectStackAsync(stackId, ct);
    }

    public Task<bool> IsRunningAsync(CancellationToken ct = default) => Current.IsRunningAsync(ct);
}
