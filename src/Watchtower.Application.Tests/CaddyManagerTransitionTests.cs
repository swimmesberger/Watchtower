using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins the decision seam behind the runtime proxy toggle and provider switch (ADR-0015): which
/// options change triggers a full reconcile, a data-plane teardown, a config refresh — or nothing.
/// The options monitor fires for every Watchtower options reload, so "unrelated change ⇒ None" is as
/// load-bearing as the transitions, and a provider switch must Stop one side while Starting the other.
/// </summary>
public sealed class CaddyManagerTransitionTests {
    // Caddy is named explicitly rather than left to the default, which is the in-process provider since
    // ADR-0020: these fixtures are about what the *Caddy* manager does, and leaning on whichever provider
    // happens to be the default would turn every one of them into a test of the default instead.
    private static readonly ProxyOptions Disabled = new() { Enabled = false, Provider = ProxyProviderNames.Caddy };
    private static readonly ProxyOptions Enabled = new() { Enabled = true, Provider = ProxyProviderNames.Caddy };
    private static readonly ProxyOptions EnabledCloudflare = new() { Enabled = true, Provider = "cloudflare" };

    [Fact]
    public void SameOptions_NoTransition() =>
        Assert.Equal(ProxyTransition.None, CaddyManager.DecideTransition(Enabled, Enabled with { }));

    [Fact]
    public void EnablingStartsTheProxy() =>
        Assert.Equal(ProxyTransition.Start, CaddyManager.DecideTransition(Disabled, Enabled));

    [Fact]
    public void DisablingTearsTheProxyDown() =>
        Assert.Equal(ProxyTransition.Stop, CaddyManager.DecideTransition(Enabled, Disabled));

    [Fact]
    public void ChangingTheEmailWhileEnabled_RefreshesTheConfig() =>
        Assert.Equal(
            ProxyTransition.Refresh,
            CaddyManager.DecideTransition(Enabled, Enabled with { AdminEmail = "ops@example.com" }));

    [Fact]
    public void ChangesWhileDisabled_DoNothing() =>
        Assert.Equal(
            ProxyTransition.None,
            CaddyManager.DecideTransition(Disabled, Disabled with { CaddyImage = "caddy:2.8" }));

    // ── Provider switch: the same options change lands differently on each provider ──

    [Fact]
    public void SwitchingToCloudflare_StopsCaddy_AndStartsCloudflare() {
        Assert.Equal(ProxyTransition.Stop, CaddyManager.DecideTransition(Enabled, EnabledCloudflare));
        Assert.Equal(ProxyTransition.Start, CloudflareTunnelProvider.DecideTransition(Enabled, EnabledCloudflare));
    }

    [Fact]
    public void SwitchingBackToCaddy_StopsCloudflare_AndStartsCaddy() {
        Assert.Equal(ProxyTransition.Start, CaddyManager.DecideTransition(EnabledCloudflare, Enabled));
        Assert.Equal(ProxyTransition.Stop, CloudflareTunnelProvider.DecideTransition(EnabledCloudflare, Enabled));
    }

    /// <summary>
    /// The default provider is the in-process one (ADR-0020), so enabling the proxy without naming a
    /// backend must leave the Caddy container alone. This is the transition an operator who never had
    /// Caddy takes, and the one the default flip made possible in the first place.
    /// </summary>
    [Fact]
    public void EnablingWithTheDefaultProvider_DoesNotStartCaddy() {
        var disabledDefault = new ProxyOptions { Enabled = false };
        var enabledDefault = new ProxyOptions { Enabled = true };
        Assert.Equal(ProxyTransition.None, CaddyManager.DecideTransition(disabledDefault, enabledDefault));
        Assert.Equal(
            ProxyTransition.Start, YarpProxyProvider.DecideTransition(disabledDefault, enabledDefault));
    }

    [Fact]
    public void EnablingWithCloudflareSelected_DoesNotStartCaddy() {
        var disabledCloudflare = Disabled with { Provider = "cloudflare" };
        Assert.Equal(ProxyTransition.None, CaddyManager.DecideTransition(disabledCloudflare, EnabledCloudflare));
        Assert.Equal(ProxyTransition.Start, CloudflareTunnelProvider.DecideTransition(disabledCloudflare, EnabledCloudflare));
    }

    [Fact]
    public void CloudflareTokenChangeWhileActive_Refreshes() {
        var next = EnabledCloudflare with { Cloudflare = new CloudflareProxyOptions { ApiToken = "t2" } };
        Assert.Equal(ProxyTransition.Refresh, CloudflareTunnelProvider.DecideTransition(EnabledCloudflare, next));
        Assert.Equal(ProxyTransition.None, CaddyManager.DecideTransition(EnabledCloudflare, next));
    }
}
