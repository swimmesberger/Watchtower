using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The one place that decides whether the in-process proxy's ingress listeners exist. Every assertion here
/// is about the projection an operator cannot see: the endpoints Kestrel is handed are derived from the
/// reverse-proxy settings, and whatever the environment says about <c>Kestrel:Endpoints:ProxyHttp*</c> is
/// masked out on the way.
/// </summary>
public sealed class ProxyIngressKestrelConfigurationTests {
    [Fact]
    public void WithTheProxyEnabled_BothEndpointsAreDerived() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp")));

        Assert.Equal("http://+:8081", section["Endpoints:ProxyHttp:Url"]);
        Assert.Equal("https://+:8443", section["Endpoints:ProxyHttps:Url"]);
    }

    [Fact]
    public void ConfiguredPorts_AreTheOnesProjected() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Watchtower:Proxy:Yarp:HttpPort", "18081"),
            ("Watchtower:Proxy:Yarp:HttpsPort", "18443")));

        Assert.Equal("http://+:18081", section["Endpoints:ProxyHttp:Url"]);
        Assert.Equal("https://+:18443", section["Endpoints:ProxyHttps:Url"]);
    }

    /// <summary>Disabled, or another provider: nothing binds. No idle TLS listener behind Caddy.</summary>
    [Theory]
    [InlineData("false", "yarp")]
    [InlineData("true", "caddy")]
    [InlineData("true", "cloudflare")]
    public void WithoutTheInProcessProvider_ThereAreNoProxyEndpoints(string enabled, string provider) {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", enabled), ("Watchtower:Proxy:Provider", provider)));

        Assert.Null(section["Endpoints:ProxyHttp:Url"]);
        Assert.Null(section["Endpoints:ProxyHttps:Url"]);
    }

    /// <summary>An unknown or blank provider resolves to yarp, exactly as <c>ProxyOptions</c> does.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("nginx")]
    public void AnUnrecognisedProvider_IsTheInProcessOne(string provider) {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", provider)));

        Assert.Equal("http://+:8081", section["Endpoints:ProxyHttp:Url"]);
    }

    /// <summary>Port 0 is the operator saying "not that listener" — the endpoint simply is not projected.</summary>
    [Fact]
    public void PortZero_TurnsOneListenerOff() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Watchtower:Proxy:Yarp:HttpsPort", "0")));

        Assert.Equal("http://+:8081", section["Endpoints:ProxyHttp:Url"]);
        Assert.Null(section["Endpoints:ProxyHttps:Url"]);
    }

    /// <summary>
    /// The masking that retires the old "blank means off" filter. These keys used to be the knob and may
    /// still be sitting in an operator's compose file; a blank one would fail Kestrel's loader on startup
    /// and a stale one would fight the derivation, so neither reaches it.
    /// </summary>
    [Fact]
    public void StrayProxyEndpointKeys_AreMasked() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Kestrel:Endpoints:ProxyHttp:Url", ""),
            ("Kestrel:Endpoints:ProxyHttps:Url", "https://+:9999"),
            ("Kestrel:Endpoints:ProxyHttps:SslProtocols:0", "Tls12")));

        Assert.Equal("http://+:8081", section["Endpoints:ProxyHttp:Url"]);
        Assert.Equal("https://+:8443", section["Endpoints:ProxyHttps:Url"]);
        Assert.Null(section["Endpoints:ProxyHttps:SslProtocols:0"]);
    }

    /// <summary>Masking is confined to the two proxy endpoints — everything else is the host's own.</summary>
    [Fact]
    public void EveryOtherKestrelKey_PassesThrough() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Kestrel:Endpoints:Http:Url", "http://+:8080"),
            ("Kestrel:Limits:MaxRequestBodySize", "1048576")));

        Assert.Equal("http://+:8080", section["Endpoints:Http:Url"]);
        Assert.Equal("1048576", section["Limits:MaxRequestBodySize"]);
    }

    /// <summary>
    /// A port that is not a number at all is off, not the shipped default. Binding a public port the
    /// operator did not name is the worse of the two ways to read a typo. (Surrounding whitespace is not a
    /// typo — <c>NumberStyles.Integer</c> trims it, and an environment variable with a stray space still
    /// means the port it says.)
    /// </summary>
    [Theory]
    [InlineData("八千四百四十三")]
    [InlineData("8443x")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void AnUnreadablePort_IsOffRatherThanTheDefault(string configured) {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Watchtower:Proxy:Yarp:HttpsPort", configured)));

        Assert.Null(section["Endpoints:ProxyHttps:Url"]);
        Assert.Equal("http://+:8081", section["Endpoints:ProxyHttp:Url"]);
    }

    /// <summary>
    /// And an unreadable <c>Proxy:Enabled</c> is off rather than an exception. This runs before the host
    /// exists, so a typo'd environment variable would otherwise be a stack trace instead of a listener.
    /// </summary>
    [Fact]
    public void AnUnreadableEnabledFlag_IsOff() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(("Watchtower:Proxy:Enabled", "yes please")));

        Assert.Null(section["Endpoints:ProxyHttp:Url"]);
        Assert.Null(section["Endpoints:ProxyHttps:Url"]);
    }

    // ── The management port is never ingress ──────────────────────────────────

    /// <summary>
    /// An ingress port that collides with the management endpoint is dropped, not bound. Two endpoints on
    /// one port is a duplicate bind, and classifying the management port as ingress is the exact confusion
    /// the endpoint split exists to prevent — an unrouted host would stop reaching the UI.
    /// </summary>
    [Fact]
    public void AnIngressPortOnTheManagementPort_IsRefused() {
        var warnings = new ProxyIngressWarnings();
        var section = ProxyIngressKestrelConfiguration.Build(
            Root(
                ("Watchtower:Proxy:Enabled", "true"),
                ("Watchtower:Proxy:Provider", "yarp"),
                ("Watchtower:Proxy:Yarp:HttpPort", "8080"),
                ("Kestrel:Endpoints:Http:Url", "http://+:8080")),
            warnings);

        Assert.Null(section["Endpoints:ProxyHttp:Url"]);
        // The other listener is unaffected — one bad port is not a reason to take ingress down entirely.
        Assert.Equal("https://+:8443", section["Endpoints:ProxyHttps:Url"]);
        Assert.Equal("http://+:8080", section["Endpoints:Http:Url"]);
    }

    /// <summary>The same holds against a management endpoint that came from the hosting URLs.</summary>
    [Fact]
    public void TheCollisionIsCheckedAgainstAHostingUrlToo() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Yarp:HttpsPort", "5080"),
            ("urls", "http://localhost:5080")));

        Assert.Null(section["Endpoints:ProxyHttps:Url"]);
        Assert.Equal("http://+:8081", section["Endpoints:ProxyHttp:Url"]);
    }

    /// <summary>Reported once, however many times an unrelated settings write re-runs the projection.</summary>
    [Fact]
    public void TheCollisionIsWarnedAboutOnce() {
        var reported = new List<string>();
        var warnings = new ProxyIngressWarnings();
        warnings.UseLogger(new CollectingLogger(reported));
        var settings = new ReloadableSettings(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Yarp:HttpPort", "8080"),
            ("Kestrel:Endpoints:Http:Url", "http://+:8080"));

        var section = ProxyIngressKestrelConfiguration.Build(
            new ConfigurationBuilder().Add(settings).Build(), warnings);
        settings.Publish(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Yarp:HttpPort", "8080"),
            ("Kestrel:Endpoints:Http:Url", "http://+:8080"),
            ("Watchtower:Proxy:AdminEmail", "ops@example.invalid"));

        Assert.Null(section["Endpoints:ProxyHttp:Url"]);
        Assert.Single(reported);
        Assert.Contains("8080", reported[0], StringComparison.Ordinal);
    }

    // ── The management endpoint on a host that had none ───────────────────────

    /// <summary>
    /// Kestrel binds the hosting URLs only while no endpoint is configured at all. The moment ingress adds
    /// one they are overridden with a warning — so a bare <c>dotnet run</c> or systemd host that enables
    /// the proxy would lose its management listener at the next start unless the hosting URL is promoted
    /// into a named endpoint here.
    /// </summary>
    [Fact]
    public void WithIngressAndNoManagementEndpoint_TheHostingUrlIsProjected() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("urls", "http://localhost:5080")));

        Assert.Equal("http://localhost:5080", section["Endpoints:Http:Url"]);
        Assert.Equal("http://+:8081", section["Endpoints:ProxyHttp:Url"]);
    }

    /// <summary>
    /// And with the proxy off it is not — nothing is projected at all, so development, Aspire and the
    /// integration tests bind exactly as they always did.
    /// </summary>
    [Fact]
    public void WithoutIngress_TheHostingUrlIsLeftAlone() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "false"), ("urls", "http://localhost:5080")));

        Assert.Null(section["Endpoints:Http:Url"]);
    }

    /// <summary>An explicitly configured management endpoint always wins over the hosting URLs.</summary>
    [Fact]
    public void AConfiguredManagementEndpoint_IsNotOverwritten() {
        var section = ProxyIngressKestrelConfiguration.Build(Root(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Kestrel:Endpoints:Http:Url", "http://+:8080"),
            ("urls", "http://localhost:5080")));

        Assert.Equal("http://+:8080", section["Endpoints:Http:Url"]);
    }

    // ── Reload ────────────────────────────────────────────────────────────────

    [Fact]
    public void EnablingTheProxy_RaisesTheProjectionsReloadToken() {
        var settings = new ReloadableSettings(("Watchtower:Proxy:Enabled", "false"));
        var section = ProxyIngressKestrelConfiguration.Build(
            new ConfigurationBuilder().Add(settings).Build());
        var reloads = 0;
        ChangeToken.OnChange(section.GetReloadToken, () => reloads++);

        settings.Publish(("Watchtower:Proxy:Enabled", "true"));

        Assert.Equal(1, reloads);
        Assert.Equal("https://+:8443", section["Endpoints:ProxyHttps:Url"]);
    }

    /// <summary>
    /// And a settings write that changes nothing the listeners depend on does not. Almost every write is
    /// one of those, and rebinding a public listener on each would be a self-inflicted outage.
    /// </summary>
    [Fact]
    public void AnUnrelatedSettingsWrite_DoesNotReload() {
        var settings = new ReloadableSettings(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:AdminEmail", "ops@example.invalid"));
        var section = ProxyIngressKestrelConfiguration.Build(
            new ConfigurationBuilder().Add(settings).Build());
        var reloads = 0;
        ChangeToken.OnChange(section.GetReloadToken, () => reloads++);

        settings.Publish(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:AdminEmail", "other@example.invalid"));

        Assert.Equal(0, reloads);
    }

    [Fact]
    public void DerivePorts_IsTheSameDecision() {
        Assert.Equal(
            (8081, 8443),
            ProxyIngressKestrelConfiguration.DerivePorts(Root(("Watchtower:Proxy:Enabled", "true"))));
        Assert.Equal(
            (null, null),
            ProxyIngressKestrelConfiguration.DerivePorts(Root(("Watchtower:Proxy:Enabled", "false"))));
    }

    /// <summary>Collects the warning text, so a test can assert on what an operator would be told.</summary>
    private sealed class CollectingLogger(List<string> lines) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));
    }

    private static IConfiguration Root(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
