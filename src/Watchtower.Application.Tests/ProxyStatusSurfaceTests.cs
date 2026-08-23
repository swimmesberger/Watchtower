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
        Assert.Equal("HTTPS ingress is not configured — routes are served over plain HTTP only", status.ProviderDetail);
    }

    [Fact]
    public async Task OnceTheHttpsListenerIsBound_ThereIsNothingToReport() {
        using var host = YarpHost();
        host.Services.GetRequiredService<YarpListenerState>().Update(s => s with { HttpsBound = true });

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
        host.Services.GetRequiredService<YarpListenerState>().Update(s => s with { HttpsBound = bound });

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetProxyConfig>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetProxyConfig.Query(), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(bound, result.Value.Config.Yarp.HttpsListenerBound);
        // The rest of the block is configuration, and the secret is only ever a flag.
        Assert.Equal(new YarpProxyOptions().AcmeDirectoryUrl, result.Value.Config.Yarp.AcmeDirectoryUrl);
        Assert.False(result.Value.Config.Yarp.HasAcmeEabHmacKey);
    }

    /// <summary>
    /// TLS ingress turned off on purpose, which is an ordinary thing to do behind another terminator —
    /// so it is reported as the choice it is rather than as the "never came up" alarm.
    /// </summary>
    [Fact]
    public async Task WithTlsIngressTurnedOff_TheStatusSaysWhy() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp),
            ("Watchtower:Proxy:Yarp:HttpsPort", "0"));

        var status = await StatusAsync(host);

        Assert.Equal(
            "HTTPS ingress disabled (port 0) — routes are served over plain HTTP only",
            status.ProviderDetail);
    }

    /// <summary>Plain-HTTP ingress off is worth saying too: no certificate can be issued over HTTP-01.</summary>
    [Fact]
    public async Task WithPlainIngressTurnedOff_TheStatusSaysWhatItCosts() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp),
            ("Watchtower:Proxy:Yarp:HttpPort", "0"));
        host.Services.GetRequiredService<YarpListenerState>().Update(s => s with { HttpsBound = true });

        var status = await StatusAsync(host);

        Assert.Equal(
            "HTTP ingress disabled (port 0) — ACME HTTP-01 validation cannot reach this instance",
            status.ProviderDetail);
    }

    /// <summary>
    /// Both listeners off: the caveats are additive, because turning TLS ingress off does not stop the
    /// missing plain-HTTP listener from being the reason no certificate can be issued.
    /// </summary>
    [Fact]
    public async Task WithBothIngressPortsOff_BothNotesAreShown() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp),
            ("Watchtower:Proxy:Yarp:HttpPort", "0"),
            ("Watchtower:Proxy:Yarp:HttpsPort", "0"));

        var status = await StatusAsync(host);

        Assert.Contains("HTTPS ingress disabled (port 0)", status.ProviderDetail!, StringComparison.Ordinal);
        Assert.Contains("HTTP ingress disabled (port 0)", status.ProviderDetail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A port that was asked for but never became a listener — today, one that collided with the
    /// management port and was refused by the projection. Without this the only trace is a single warning
    /// in the log, and the Settings page would show the port as if it were serving.
    /// </summary>
    [Fact]
    public async Task AnIngressPortThatWasRefused_IsReported() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp),
            ("Watchtower:Proxy:Yarp:HttpPort", "8080"));
        // What the projection produced: the TLS listener, and no plain-HTTP one.
        host.Services.GetRequiredService<YarpListenerState>().Update(s => s with {
            HttpsBound = true, IngressPorts = new HashSet<int> { 8443 }, ManagementPort = 8080,
        });

        var status = await StatusAsync(host);

        Assert.Equal(
            "HTTP ingress port 8080 was refused (see the logs) — "
            + "ACME HTTP-01 validation cannot reach this instance",
            status.ProviderDetail);
    }

    /// <summary>
    /// The one alarm in here: the configuration asks for a listener the server is not listening on. Only
    /// reachable by moving a port at runtime onto one something else holds — a bind failure at startup is
    /// fatal — and otherwise invisible, because the Settings page would go on reporting the port as
    /// configured while nothing arrived on it.
    /// </summary>
    [Fact]
    public async Task AnIngressPortThatDidNotBind_IsReported() {
        using var host = YarpHost();
        host.Services.GetRequiredService<YarpListenerState>().Update(s => s with {
            HttpsBound = true, IngressPorts = new HashSet<int> { 8081, 8443 },
        });

        // The server is listening on 8081 but never came up on 8443.
        var status = await StatusAsync(host, new StubBoundPorts(new HashSet<int> { 8081 }));

        Assert.Equal("ingress port 8443 failed to bind — see the logs", status.ProviderDetail);
    }

    /// <summary>
    /// And where the server cannot say what it bound — the unit-test hosts, <c>TestServer</c> — nothing is
    /// claimed. "Cannot tell" must never be reported as "nothing is bound".
    /// </summary>
    [Fact]
    public async Task WithNoAddressesToRead_NothingIsClaimed() {
        using var host = YarpHost();
        host.Services.GetRequiredService<YarpListenerState>().Update(s => s with {
            HttpsBound = true, IngressPorts = new HashSet<int> { 8081, 8443 },
        });

        var status = await StatusAsync(host, new StubBoundPorts(null));

        Assert.Null(status.ProviderDetail);
    }

    /// <summary>The ports ride the config surface, so the Settings page can offer them as fields.</summary>
    [Fact]
    public async Task TheConfigSurfaceReportsTheIngressPorts() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Yarp:HttpPort", "18081"), ("Watchtower:Proxy:Yarp:HttpsPort", "0"));

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetProxyConfig>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetProxyConfig.Query(), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(18081, result.Value.Config.Yarp.HttpPort);
        Assert.Equal(0, result.Value.Config.Yarp.HttpsPort);
    }

    private static AuthTestHost YarpHost() => AuthTestHost.Start(
        ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp));

    private static async Task<GetProxyStatus.Response> StatusAsync(
        AuthTestHost host, BoundListenerPorts? bound = null) {
        await using var scope = host.Services.CreateAsyncScope();
        object[] overrides = bound is null ? [] : [bound];
        var handler = ActivatorUtilities.CreateInstance<GetProxyStatus>(scope.ServiceProvider, overrides);
        var result = await handler.HandleAsync(new GetProxyStatus.Query(), Ct);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>What the server bound, stated rather than read off a socket there is none of.</summary>
    private sealed class StubBoundPorts(IReadOnlySet<int>? ports) : BoundListenerPorts(null!) {
        public override IReadOnlySet<int>? Current => ports;
    }
}
