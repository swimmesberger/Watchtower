using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The in-process provider's half of the transition seam (see <see cref="CaddyManagerTransitionTests"/>
/// for the other two). Every provider computes its own transition from the same options change, so a
/// provider switch has to Stop one side while Starting the other — and a provider that is not selected
/// must stay entirely out of the way.
/// </summary>
public sealed class YarpProxyProviderTransitionTests {
    private static readonly ProxyOptions DisabledYarp = new() { Enabled = false, Provider = "yarp" };
    private static readonly ProxyOptions EnabledYarp = new() { Enabled = true, Provider = "yarp" };
    // Named explicitly: since ADR-0017 the unstated default is yarp, so `new() { Enabled = true }` is the
    // in-process provider and would make the two "switch to Caddy" cases below assert nothing at all.
    private static readonly ProxyOptions EnabledCaddy = new() { Enabled = true, Provider = "caddy" };
    private static readonly ProxyOptions EnabledCloudflare = new() { Enabled = true, Provider = "cloudflare" };

    [Fact]
    public void EnablingWithYarpSelected_StartsOnlyYarp() {
        Assert.Equal(ProxyTransition.Start, YarpProxyProvider.DecideTransition(DisabledYarp, EnabledYarp));
        Assert.Equal(ProxyTransition.None, CaddyManager.DecideTransition(DisabledYarp, EnabledYarp));
        Assert.Equal(ProxyTransition.None, CloudflareTunnelProvider.DecideTransition(DisabledYarp, EnabledYarp));
    }

    [Fact]
    public void SwitchingFromYarpToCaddy_StopsYarp_AndStartsCaddy() {
        Assert.Equal(ProxyTransition.Stop, YarpProxyProvider.DecideTransition(EnabledYarp, EnabledCaddy));
        Assert.Equal(ProxyTransition.Start, CaddyManager.DecideTransition(EnabledYarp, EnabledCaddy));
    }

    [Fact]
    public void SwitchingFromCaddyToYarp_StopsCaddy_AndStartsYarp() {
        Assert.Equal(ProxyTransition.Start, YarpProxyProvider.DecideTransition(EnabledCaddy, EnabledYarp));
        Assert.Equal(ProxyTransition.Stop, CaddyManager.DecideTransition(EnabledCaddy, EnabledYarp));
    }

    [Fact]
    public void SwitchingFromYarpToCloudflare_StopsYarp_AndStartsCloudflare() {
        Assert.Equal(ProxyTransition.Stop, YarpProxyProvider.DecideTransition(EnabledYarp, EnabledCloudflare));
        Assert.Equal(ProxyTransition.Start, CloudflareTunnelProvider.DecideTransition(EnabledYarp, EnabledCloudflare));
    }

    [Fact]
    public void DisablingWhileYarpIsActive_TearsItDown() =>
        Assert.Equal(ProxyTransition.Stop, YarpProxyProvider.DecideTransition(EnabledYarp, DisabledYarp));

    [Fact]
    public void AnAcmeChangeWhileYarpIsActive_RefreshesOnlyYarp() {
        var next = EnabledYarp with {
            Yarp = new YarpProxyOptions { AcmeDirectoryUrl = "https://acme-staging.example.invalid/directory" },
        };
        Assert.Equal(ProxyTransition.Refresh, YarpProxyProvider.DecideTransition(EnabledYarp, next));
        Assert.Equal(ProxyTransition.None, CaddyManager.DecideTransition(EnabledYarp, next));
    }

    [Fact]
    public void AnUnrelatedChangeWhileYarpIsActive_StillRefreshesIt() {
        // ProxyTransitions.Decide is deliberately coarse: it knows "still active, and the options record
        // differs", not which field moved. So a CaddyImage edit refreshes the in-process provider too.
        // That is cheap here — a refresh re-projects an in-memory table — and the alternative, a
        // per-provider field diff, is a list that would silently go stale as options are added.
        var next = EnabledYarp with { CaddyImage = "caddy:2.8" };
        Assert.Equal(ProxyTransition.Refresh, YarpProxyProvider.DecideTransition(EnabledYarp, next));
    }

    [Fact]
    public void ChangesWhileDisabled_DoNothing() =>
        Assert.Equal(
            ProxyTransition.None,
            YarpProxyProvider.DecideTransition(DisabledYarp, DisabledYarp with { CaddyImage = "caddy:2.8" }));

    [Fact]
    public void AnIdenticalOptionsReload_IsIgnored() =>
        Assert.Equal(ProxyTransition.None, YarpProxyProvider.DecideTransition(EnabledYarp, EnabledYarp with { }));
}
