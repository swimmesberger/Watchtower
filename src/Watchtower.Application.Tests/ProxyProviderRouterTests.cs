using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The router resolves the selected backend per call, which is what makes <c>Proxy:Provider</c>
/// switchable without a restart. Pinned two ways: only the selected provider reports itself active,
/// and a call through the router actually lands in that provider's code.
/// </summary>
public sealed class ProxyProviderRouterTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("yarp", typeof(YarpProxyProvider))]
    [InlineData("caddy", typeof(CaddyManager))]
    [InlineData("cloudflare", typeof(CloudflareTunnelProvider))]
    public void TheRouterServesTheSelectedProvider(string provider, Type expected) {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", provider));

        var router = host.Services.GetRequiredService<IProxyProvider>();
        Assert.True(router.Enabled);

        // Every provider self-gates on the same options, so exactly the selected one is active.
        foreach (var candidate in new IProxyProvider[] {
            host.Services.GetRequiredService<YarpProxyProvider>(),
            host.Services.GetRequiredService<CaddyManager>(),
            host.Services.GetRequiredService<CloudflareTunnelProvider>(),
        })
            Assert.Equal(candidate.GetType() == expected, candidate.Enabled);
    }

    [Fact]
    public async Task WithYarpSelected_ACallThroughTheRouterReachesTheInProcessProvider() {
        var certs = new RecordingProxyCertificateManager();
        using var host = Start(certs, ProxyProviderNames.Yarp);

        await host.Services.GetRequiredService<IProxyProvider>()
            .ForgetDomainAsync("app.example.invalid", actor: "admin", Ct);

        // Only YarpProxyProvider talks to the certificate manager, so reaching it is proof the router
        // dispatched there rather than to one of the container-managing providers.
        Assert.Equal(["app.example.invalid"], certs.ForgottenHosts);
    }

    [Fact]
    public async Task WithCaddySelected_TheInProcessProviderIsNotReached() {
        var certs = new RecordingProxyCertificateManager();
        using var host = Start(certs, ProxyProviderNames.Caddy);

        await host.Services.GetRequiredService<IProxyProvider>()
            .ForgetDomainAsync("app.example.invalid", actor: "admin", Ct);

        // Caddy regenerates its config from the route table and holds nothing per hostname.
        Assert.Empty(certs.ForgottenHosts);
    }

    [Fact]
    public void WithTheProxyDisabled_NoProviderIsActive() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp));

        Assert.False(host.Services.GetRequiredService<IProxyProvider>().Enabled);
        Assert.False(host.Services.GetRequiredService<YarpProxyProvider>().Enabled);
    }

    /// <summary>A host whose certificate manager is the recording double — registered last, so it wins.</summary>
    private static AuthTestHost Start(RecordingProxyCertificateManager certs, string provider) =>
        AuthTestHost.Start(
            services => services.AddSingleton<IProxyCertificateManager>(certs),
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", provider));
}
