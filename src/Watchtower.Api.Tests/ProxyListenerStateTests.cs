using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Api.Proxy;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// What the in-process proxy's host wiring does when there is no listener to speak of, and how the
/// listener facts follow the projected Kestrel section once there is. Everything the proxy reports about
/// its own data plane comes from that derivation — there is no container to inspect.
/// </summary>
public sealed class ProxyListenerStateTests {
    [Fact]
    public async Task UnderTestServer_TheAppStarts_AndReportsNoHttpsListener() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // The listener state is derived before the host is even built, and TestServer binding nothing has
        // to be a shrug rather than a crash.
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
    /// The store is constructible over the configured directory — the check that would otherwise only
    /// happen inside a container, since nothing in the test host resolves it on its own.
    /// </summary>
    [Fact]
    public void TheCertificateStore_OpensOverTheConfiguredDirectory() {
        using var factory = new WatchtowerApiFactory();

        var store = factory.Services.GetRequiredService<CertificateStore>();

        Assert.True(Directory.Exists(store.RootPath));
        Assert.Empty(store.Entries);
        Assert.Null(store.SelectContext("app.example.invalid"));
    }

    // ── Which port is ingress, and which is management ────────────────────────

    /// <summary>
    /// The shipped image's shape. A port on its own says nothing; what makes 8081 and 8443 ingress and 8080
    /// the management plane is the <c>Endpoints:*</c> names they are projected under, which is why the
    /// derivation reads the section rather than a list of ports.
    /// </summary>
    [Fact]
    public void IngressPorts_AreTheProjectedProxyEndpoints() {
        var snapshot = ProxyListenerStateInitializer.Derive(ShippedEndpoints(), hostingUrls: null);

        Assert.Equal([8081, 8443], snapshot.IngressPorts.Order());
        Assert.Equal(8080, snapshot.ManagementPort);
        Assert.True(snapshot.HttpsBound);
    }

    /// <summary>
    /// TLS ingress turned off — the operator set the port to 0, or another terminator fronts Watchtower.
    /// The plain-HTTP endpoint is still ingress, and the proxy says HTTPS is not there.
    /// </summary>
    [Fact]
    public void WithoutTheTlsEndpoint_OnlyThePlainPortIsIngress() {
        var snapshot = ProxyListenerStateInitializer.Derive(
            Section(("Endpoints:Http:Url", "http://+:8080"), ("Endpoints:ProxyHttp:Url", "http://+:8081")),
            hostingUrls: null);

        Assert.Equal([8081], snapshot.IngressPorts.Order());
        Assert.False(snapshot.HttpsBound);
    }

    /// <summary>
    /// The ACME self-check dials whatever this names, and what it has to dial is the listener the CA
    /// reaches: the operator publishes port 80 onto <c>ProxyHttp</c>, not onto the management endpoint.
    /// </summary>
    [Fact]
    public void LocalHttpAddress_PrefersTheIngressHttpEndpoint() {
        var snapshot = ProxyListenerStateInitializer.Derive(ShippedEndpoints(), hostingUrls: null);

        // The wildcard bind is rewritten into something this process can dial, port preserved.
        Assert.Equal("http://127.0.0.1:8081", snapshot.LocalHttpAddress);
    }

    /// <summary>Without an ingress HTTP endpoint it is the management listener, as it always was.</summary>
    [Fact]
    public void LocalHttpAddress_FallsBackToTheManagementEndpoint() {
        var snapshot = ProxyListenerStateInitializer.Derive(
            Section(("Endpoints:Http:Url", "http://+:8080")), hostingUrls: null);

        Assert.Equal("http://127.0.0.1:8080", snapshot.LocalHttpAddress);
        Assert.Empty(snapshot.IngressPorts);
        Assert.Equal(8080, snapshot.ManagementPort);
    }

    /// <summary>
    /// A host with no named endpoints at all — development, Aspire, <c>ASPNETCORE_URLS</c>. Its single
    /// listener is the management plane by definition, and there is no ingress to separate from it.
    /// </summary>
    [Fact]
    public void WithNoEndpoints_TheManagementPortComesFromTheHostingUrls() {
        var snapshot = ProxyListenerStateInitializer.Derive(
            Section(), hostingUrls: "http://localhost:5080;https://localhost:5443");

        Assert.Empty(snapshot.IngressPorts);
        Assert.Equal(5080, snapshot.ManagementPort);
        Assert.Equal("http://127.0.0.1:5080", snapshot.LocalHttpAddress);
        Assert.False(snapshot.HttpsBound);
    }

    /// <summary>And with nothing to go on at all, the state says so rather than inventing a port.</summary>
    [Fact]
    public void WithNothingConfigured_NothingIsClaimed() {
        var snapshot = ProxyListenerStateInitializer.Derive(Section(), hostingUrls: null);

        Assert.Empty(snapshot.IngressPorts);
        Assert.Null(snapshot.ManagementPort);
        Assert.Null(snapshot.LocalHttpAddress);
    }

    // ── Following the settings at runtime ─────────────────────────────────────

    /// <summary>
    /// The facts are in place before the host has started — not after <c>ApplicationStarted</c>, which
    /// fires only once every hosted service has. Kestrel accepts connections on the ingress ports
    /// throughout that window, and an empty <c>IngressPorts</c> there is not a missing diagnostic: it is
    /// the dispatcher's fall-through rule in force on ports published to the internet.
    /// </summary>
    [Fact]
    public async Task Register_DerivesThePortsBeforeTheHostEverStarts() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(Settings(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Kestrel:Endpoints:Http:Url", "http://+:8080")));
        builder.Services.AddSingleton<YarpListenerState>();
        builder.Services.AddSingleton<ProxyIngressWarnings>();
        await using var app = builder.Build();
        var section = ProxyIngressKestrelConfiguration.Build(app.Configuration);

        ProxyListenerStateInitializer.Register(app, section);

        // Nothing has started, nothing has bound, and the rule is already in force.
        var state = app.Services.GetRequiredService<YarpListenerState>();
        Assert.Equal([8081, 8443], state.IngressPorts.Order());
        Assert.Equal(8080, state.ManagementPort);
        Assert.True(state.HttpsBound);
    }

    /// <summary>
    /// And the state follows the settings afterwards, through the subscription rather than through a
    /// second call: a settings write is what an operator actually does, and the chain that has to survive
    /// it runs settings → root reload → projection → this state. Turning the proxy off takes the ingress
    /// ports with it, which is the whole point of the endpoints being derived rather than baked into the
    /// image.
    /// </summary>
    [Fact]
    public async Task TheStateFollowsASettingsWrite() {
        var settings = new ReloadableSettings(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Kestrel:Endpoints:Http:Url", "http://+:8080"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        ((IConfigurationBuilder)builder.Configuration).Add(settings);
        builder.Services.AddSingleton<YarpListenerState>();
        builder.Services.AddSingleton<ProxyIngressWarnings>();
        await using var app = builder.Build();
        var section = ProxyIngressKestrelConfiguration.Build(app.Configuration);

        ProxyListenerStateInitializer.Register(app, section);
        var state = app.Services.GetRequiredService<YarpListenerState>();
        Assert.Equal([8081, 8443], state.IngressPorts.Order());

        // Nothing calls Apply here — the write is the only input.
        settings.Publish(
            ("Watchtower:Proxy:Enabled", "false"), ("Kestrel:Endpoints:Http:Url", "http://+:8080"));

        Assert.Empty(state.IngressPorts);
        Assert.False(state.HttpsBound);
        Assert.Equal(8080, state.ManagementPort);
        Assert.Equal("http://127.0.0.1:8080", state.LocalHttpAddress);
    }

    /// <summary>The shipped image's projected section: management on 8080, ingress on 8081/8443.</summary>
    private static IConfiguration ShippedEndpoints() => Section(
        ("Endpoints:Http:Url", "http://+:8080"),
        ("Endpoints:ProxyHttp:Url", "http://+:8081"),
        ("Endpoints:ProxyHttps:Url", "https://+:8443"));

    /// <summary>A stand-in for the projected section — keys are relative to <c>Kestrel</c>.</summary>
    private static IConfiguration Section(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(Settings(pairs)).Build();

    private static IEnumerable<KeyValuePair<string, string?>> Settings(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value));
}
