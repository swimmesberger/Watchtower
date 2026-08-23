using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Api.Proxy;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// What the in-process proxy's host wiring does when there is no listener to speak of. Everything phase
/// 4 adds to <c>Program.cs</c> is conditional on a Kestrel endpoint the test host never configures, and
/// the point here is that "conditional" really means inert: the app still boots, the certificate store
/// still opens over its directory, and the proxy honestly reports that nothing is bound.
/// </summary>
public sealed class ProxyListenerStateTests {
    [Fact]
    public async Task UnderTestServer_TheAppStarts_AndReportsNoHttpsListener() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // The listener-state initializer runs on ApplicationStarted, which the first request forces.
        // TestServer exposes no address feature at all, and that has to be a shrug rather than a crash.
        var health = await client.GetAsync("/health", ct);
        Assert.True(health.IsSuccessStatusCode);

        var response = await client.PostAsJsonAsync(
            "/rpc",
            new { jsonrpc = "2.0", method = "proxy.getConfig", @params = new { }, id = "1" },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains("\"httpsListenerBound\":false", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The store is filled from the database by the host's startup step — the check that would otherwise
    /// only happen inside a container, since nothing in the test host resolves it on its own. An empty
    /// table has to produce an empty store rather than a failed start.
    /// </summary>
    [Fact]
    public void TheCertificateStore_IsInitializedByTheHost() {
        using var factory = new WatchtowerApiFactory();

        var store = factory.Services.GetRequiredService<CertificateStore>();

        Assert.Empty(store.Entries);
        Assert.Null(store.SelectContext("app.example.invalid"));
    }

    // ── Which bound port is ingress, and which is management ──────────────────

    /// <summary>
    /// The shipped image's shape. A bound address is only a port; what makes 8081 and 8443 ingress and 8080
    /// the management plane is the <c>Kestrel:Endpoints:*</c> names they were configured under, which is why
    /// the derivation reads both.
    /// </summary>
    [Fact]
    public void IngressPorts_AreTheBoundProxyEndpoints() {
        var state = new YarpListenerState();

        ProxyListenerStateInitializer.Apply(
            state,
            ["http://[::]:8080", "http://[::]:8081", "https://[::]:8443"],
            ShippedEndpoints(),
            NullLogger.Instance);

        Assert.Equal([8081, 8443], state.IngressPorts.Order());
        Assert.Equal(8080, state.ManagementPort);
        Assert.True(state.HttpsBound);
    }

    /// <summary>
    /// A port that is configured but never came up is not ingress. The rule the dispatcher applies has to
    /// describe listeners that exist — refusing hosts on the strength of a listener that failed to bind
    /// would refuse them on the endpoint that is actually serving them.
    /// </summary>
    [Fact]
    public void APortThatNeverBound_IsNotIngress() {
        var state = new YarpListenerState();

        ProxyListenerStateInitializer.Apply(
            state, ["http://[::]:8080", "http://[::]:8081"], ShippedEndpoints(), NullLogger.Instance);

        Assert.Equal([8081], state.IngressPorts.Order());
        Assert.False(state.HttpsBound);
    }

    /// <summary>
    /// The ACME self-check dials whatever this names, and what it has to dial is the listener the CA
    /// reaches: the operator publishes port 80 onto <c>ProxyHttp</c>, not onto the management endpoint.
    /// </summary>
    [Fact]
    public void LocalHttpAddress_PrefersTheIngressHttpEndpoint() {
        var state = new YarpListenerState();

        ProxyListenerStateInitializer.Apply(
            state,
            ["http://[::]:8080", "http://[::]:8081", "https://[::]:8443"],
            ShippedEndpoints(),
            NullLogger.Instance);

        // Wildcard rewritten to something this process can dial, port preserved.
        Assert.Equal("http://127.0.0.1:8081", state.LocalHttpAddress);
    }

    /// <summary>Without an ingress HTTP endpoint it is the management listener, as it always was.</summary>
    [Fact]
    public void LocalHttpAddress_FallsBackToTheManagementEndpoint() {
        var state = new YarpListenerState();
        var configuration = Configuration(("Kestrel:Endpoints:Http:Url", "http://+:8080"));

        ProxyListenerStateInitializer.Apply(state, ["http://[::]:8080"], configuration, NullLogger.Instance);

        Assert.Equal("http://127.0.0.1:8080", state.LocalHttpAddress);
        Assert.Empty(state.IngressPorts);
        Assert.Equal(8080, state.ManagementPort);
    }

    /// <summary>
    /// No address feature at all — <c>TestServer</c>, or a server that exposes none. The configured URLs are
    /// the only evidence there is, and an operator who published <c>80:8081</c> is owed the ingress rule
    /// whether or not the server chose to describe itself.
    /// </summary>
    [Fact]
    public void WithNoAddressFeature_ThePortsComeFromConfiguration() {
        var state = new YarpListenerState();

        ProxyListenerStateInitializer.Apply(state, addresses: null, ShippedEndpoints(), NullLogger.Instance);

        Assert.Equal([8081, 8443], state.IngressPorts.Order());
        Assert.Equal(8080, state.ManagementPort);
    }

    /// <summary>
    /// And where the configuration names nothing either, the state is left alone rather than overwritten
    /// with an empty derivation — which is what keeps a host whose listener state was seeded elsewhere
    /// (the test factory, an Aspire run) from being flattened on startup.
    /// </summary>
    [Fact]
    public void WithNothingConfigured_TheSeededPortsSurvive() {
        var state = new YarpListenerState { IngressPorts = new HashSet<int> { 8081 }, ManagementPort = 8080 };

        ProxyListenerStateInitializer.Apply(
            state, addresses: null, Configuration(), NullLogger.Instance);

        Assert.Equal([8081], state.IngressPorts.Order());
        Assert.Equal(8080, state.ManagementPort);
    }

    /// <summary>
    /// The seed is in place the moment the initializer is registered — before <c>ApplicationStarted</c>,
    /// which fires only after every hosted service has started. Kestrel accepts connections on the ingress
    /// ports throughout that window, and an empty <c>IngressPorts</c> there is not a missing diagnostic: it
    /// is the fall-through rule in force on ports published to the internet, on every restart, for as long
    /// as the slowest hosted service takes.
    /// </summary>
    [Fact]
    public async Task Register_SeedsThePortsBeforeTheHostEverStarts() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Kestrel:Endpoints:Http:Url", "http://+:8080"),
            new KeyValuePair<string, string?>("Kestrel:Endpoints:ProxyHttp:Url", "http://+:8081"),
            new KeyValuePair<string, string?>("Kestrel:Endpoints:ProxyHttps:Url", "https://+:8443"),
        ]);
        builder.Services.AddSingleton<YarpListenerState>();
        await using var app = builder.Build();

        ProxyListenerStateInitializer.Register(app);

        // Nothing has started, nothing has bound, and the rule is already in force.
        var state = app.Services.GetRequiredService<YarpListenerState>();
        Assert.Equal([8081, 8443], state.IngressPorts.Order());
        Assert.Equal(8080, state.ManagementPort);
    }

    /// <summary>
    /// And the bind narrows the seed rather than sitting alongside it: a configured endpoint that never
    /// came up stops being ingress once the server has said what it bound.
    /// </summary>
    [Fact]
    public void TheBoundAddresses_NarrowTheSeed() {
        var state = new YarpListenerState();
        var configuration = ShippedEndpoints();
        ProxyListenerStateInitializer.Seed(state, configuration);
        Assert.Equal([8081, 8443], state.IngressPorts.Order());

        ProxyListenerStateInitializer.Apply(
            state, ["http://[::]:8080", "http://[::]:8081"], configuration, NullLogger.Instance);

        Assert.Equal([8081], state.IngressPorts.Order());
    }

    private static IConfiguration ShippedEndpoints() => Configuration(
        ("Kestrel:Endpoints:Http:Url", "http://+:8080"),
        ("Kestrel:Endpoints:ProxyHttp:Url", "http://+:8081"),
        ("Kestrel:Endpoints:ProxyHttps:Url", "https://+:8443"));

    private static IConfiguration Configuration(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
}
