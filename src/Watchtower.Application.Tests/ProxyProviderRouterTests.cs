using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.PortRoutes;
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

    // ── The port plane rides alongside, not instead (ADR-0033 addendum) ───────

    /// <summary>
    /// The two operations that mean "the route table changed" and "this stack's containers moved" reach
    /// the port plane as well as the selected provider — under every provider, because a port route's
    /// listener is on Watchtower's own container. Routing them to the provider alone is what left a
    /// Caddy or Cloudflare deployment's port routes unserved.
    /// </summary>
    [Theory]
    [InlineData(ProxyProviderNames.Yarp)]
    [InlineData(ProxyProviderNames.Caddy)]
    [InlineData(ProxyProviderNames.Cloudflare)]
    public async Task ApplyAndConnectStack_ReachTheProviderAndThePortPlane(string provider) {
        var plane = new RecordingPortRoutePlane();
        using var host = AuthTestHost.Start(
            services => services.AddSingleton<PortRoutePlane>(plane),
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", provider));
        var router = host.Services.GetRequiredService<IProxyProvider>();

        await router.ApplyAsync(Ct);
        await router.ConnectStackAsync(42, Ct);

        Assert.Equal(1, plane.Applies);
        Assert.Equal([42], plane.ConnectedStacks);
    }

    /// <summary>A host whose certificate manager is the recording double — registered last, so it wins.</summary>
    private static AuthTestHost Start(RecordingProxyCertificateManager certs, string provider) =>
        AuthTestHost.Start(
            services => services.AddSingleton<IProxyCertificateManager>(certs),
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", provider));

    /// <summary>
    /// A plane that records rather than projects. Substituted at the concrete type the router resolves,
    /// which is also the assertion that the router resolves it per call rather than capturing one.
    /// </summary>
    private sealed class RecordingPortRoutePlane() : PortRoutePlane(
        NullScopeFactory.Instance,
        networks: null!,
        table: new ProxyRouteTable(),
        internalCerts: null!,
        signal: null!,
        options: new StaticWatchtowerOptions(),
        logger: NullLogger<PortRoutePlane>.Instance) {
        public int Applies { get; private set; }
        public List<int> ConnectedStacks { get; } = [];

        public override Task ApplyAsync(CancellationToken ct = default) {
            Applies++;
            return Task.CompletedTask;
        }

        public override Task ConnectStackAsync(int stackId, CancellationToken ct = default) {
            ConnectedStacks.Add(stackId);
            return Task.CompletedTask;
        }
    }

    /// <summary>A scope factory the recording plane never uses — it overrides everything that would.</summary>
    private sealed class NullScopeFactory : IServiceScopeFactory {
        public static readonly NullScopeFactory Instance = new();

        public IServiceScope CreateScope() => throw new NotSupportedException();
    }

    private sealed class StaticWatchtowerOptions : IOptionsMonitor<WatchtowerOptions> {
        public WatchtowerOptions CurrentValue { get; } = new();

        public WatchtowerOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) => null;
    }
}
