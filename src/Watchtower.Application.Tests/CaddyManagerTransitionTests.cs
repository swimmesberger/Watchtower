using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins the decision seam behind the runtime proxy toggle: which options change triggers a full
/// reconcile, a container teardown, a config refresh — or nothing. The options monitor fires for every
/// Watchtower options reload, so "unrelated change ⇒ None" is as load-bearing as the transitions.
/// </summary>
public sealed class CaddyManagerTransitionTests {
    private static readonly ProxyOptions Disabled = new() { Enabled = false };
    private static readonly ProxyOptions Enabled = new() { Enabled = true };

    [Fact]
    public void SameOptions_NoTransition() =>
        Assert.Equal(CaddyManager.ProxyTransition.None, CaddyManager.DecideTransition(Enabled, Enabled with { }));

    [Fact]
    public void EnablingStartsTheProxy() =>
        Assert.Equal(CaddyManager.ProxyTransition.Start, CaddyManager.DecideTransition(Disabled, Enabled));

    [Fact]
    public void DisablingTearsTheProxyDown() =>
        Assert.Equal(CaddyManager.ProxyTransition.Stop, CaddyManager.DecideTransition(Enabled, Disabled));

    [Fact]
    public void ChangingTheEmailWhileEnabled_RefreshesTheConfig() =>
        Assert.Equal(
            CaddyManager.ProxyTransition.Refresh,
            CaddyManager.DecideTransition(Enabled, Enabled with { AdminEmail = "ops@example.com" }));

    [Fact]
    public void ChangesWhileDisabled_DoNothing() =>
        Assert.Equal(
            CaddyManager.ProxyTransition.None,
            CaddyManager.DecideTransition(Disabled, Disabled with { CaddyImage = "caddy:2.8" }));
}
