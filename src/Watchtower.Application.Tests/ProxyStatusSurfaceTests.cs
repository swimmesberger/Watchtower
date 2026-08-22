using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers what the Settings and Routes pages read about the in-process proxy: whether it reports
/// running, and the one caveat worth a sentence of its own — enabled, serving, but with no HTTPS
/// listener. That state is invisible from every other signal (routes resolve, the provider is active,
/// there is no container to be unhealthy), and it means every request is travelling in the clear.
/// </summary>
public sealed class ProxyStatusSurfaceTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithYarpEnabledAndNoHttpsListener_TheStatusSaysSo() {
        using var host = YarpHost();
        var status = await StatusAsync(host);

        Assert.True(status.Enabled);
        Assert.Equal(ProxyProviderNames.Yarp, status.Provider);
        Assert.False(status.CaddyRunning);
        Assert.Equal("HTTPS listener not bound — routes are served over plain HTTP only", status.ProviderDetail);
    }

    [Fact]
    public async Task OnceTheHttpsListenerIsBound_ThereIsNothingToReport() {
        using var host = YarpHost();
        host.Services.GetRequiredService<YarpListenerState>().HttpsBound = true;

        var status = await StatusAsync(host);

        // "Running" for the in-process proxy is the listener, since there is no container to inspect.
        Assert.True(status.CaddyRunning);
        Assert.Null(status.ProviderDetail);
    }

    [Fact]
    public async Task TheOtherProvidersNeverCarryADetail() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", ProxyProviderNames.Caddy));

        var status = await StatusAsync(host);

        Assert.Equal(ProxyProviderNames.Caddy, status.Provider);
        Assert.Null(status.ProviderDetail);
    }

    [Fact]
    public async Task WithTheProxyDisabled_ThereIsNoWarningToGive() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp));

        var status = await StatusAsync(host);

        // Nothing is being served, so an unbound listener is not a problem to raise.
        Assert.False(status.Enabled);
        Assert.Equal(ProxyProviderNames.Yarp, status.Provider);
        Assert.Null(status.ProviderDetail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheConfigSurfaceReportsTheListenerState(bool bound) {
        using var host = YarpHost();
        host.Services.GetRequiredService<YarpListenerState>().HttpsBound = bound;

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetProxyConfig>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetProxyConfig.Query(), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(bound, result.Value.Config.Yarp.HttpsListenerBound);
        // The rest of the block is configuration, and the secret is only ever a flag.
        Assert.Equal(new YarpProxyOptions().AcmeDirectoryUrl, result.Value.Config.Yarp.AcmeDirectoryUrl);
        Assert.False(result.Value.Config.Yarp.HasAcmeEabHmacKey);
    }

    private static AuthTestHost YarpHost() => AuthTestHost.Start(
        ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp));

    private static async Task<GetProxyStatus.Response> StatusAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetProxyStatus>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetProxyStatus.Query(), Ct);
        Assert.True(result.IsSuccess);
        return result.Value;
    }
}
