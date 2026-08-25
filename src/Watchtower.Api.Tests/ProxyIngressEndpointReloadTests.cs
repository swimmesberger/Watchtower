using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Services.Yarp;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// A real Kestrel, on real sockets, over the real projection: the whole design rests on Kestrel binding
/// and unbinding <c>Kestrel:Endpoints:*</c> as the configuration it was handed reloads, and on the named
/// endpoint's callback running again each time the endpoint comes back. This is the test that proves it
/// — the settings-store stand-in is flipped and the ports are dialled.
/// </summary>
/// <remarks>
/// Ports are taken by opening and closing a <see cref="TcpListener"/> on 0 rather than binding on 0
/// directly: a named Kestrel endpoint has to name its port in configuration, so it cannot ask the OS for
/// one. The tiny race that leaves (something else taking the port in between) is accepted.
/// <para>
/// Note that the ingress endpoints are bound on <c>+</c> — every interface — because that is what the
/// projection derives and what the container needs; only the management endpoint here is loopback. So this
/// test really does open two ports to the network on the build machine, briefly, on ports the OS handed
/// out as free.
/// </para>
/// </remarks>
public sealed class ProxyIngressEndpointReloadTests {
    [Fact]
    public async Task TheIngressEndpoints_FollowTheReverseProxySettings() {
        var (managementPort, httpPort, httpsPort) = (FreePort(), FreePort(), FreePort());
        using var chain = TestCertificates.Create("proxy.test.invalid");
        var callbackRuns = 0;

        var settings = new ReloadableSettings(("Watchtower:Proxy:Enabled", "false"));
        var app = Host(
            settings,
            managementPort,
            [
                ("Watchtower:Proxy:Yarp:HttpPort", httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("Watchtower:Proxy:Yarp:HttpsPort", httpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            chain,
            () => Interlocked.Increment(ref callbackRuns));
        await using var _ = app;
        await app.StartAsync(TestContext.Current.CancellationToken);

        // (a) The proxy is off, so the ingress listeners do not exist — only the management endpoint does.
        Assert.True(await Accepts(managementPort));
        Assert.False(await Accepts(httpPort));
        Assert.False(await Accepts(httpsPort));
        Assert.Equal(0, callbackRuns);

        // (b) Enable it, exactly as proxy.updateConfig does. Both listeners come up, with no restart.
        settings.Publish(("Watchtower:Proxy:Enabled", "true"));
        Assert.True(await Eventually(() => Accepts(httpPort)));
        Assert.True(await Eventually(() => Accepts(httpsPort)));
        Assert.True(callbackRuns >= 1);
        Assert.True(await Accepts(managementPort));

        // …and the TLS one is ours: our SNI callback is what served the handshake.
        Assert.Equal("CN=proxy.test.invalid", await HandshakeSubject(httpsPort, "proxy.test.invalid"));

        var runsAfterFirstAdd = callbackRuns;

        // (c) Switch to another provider. The ingress listeners go away; the management plane never
        // flinches, which is what keeps the Settings page that made the change reachable.
        settings.Publish(("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "caddy"));
        Assert.True(await Eventually(async () => !await Accepts(httpPort)));
        Assert.True(await Eventually(async () => !await Accepts(httpsPort)));
        Assert.True(await Accepts(managementPort));

        // (e) Back again: the named endpoint's callback really does run a second time, which is what
        // re-attaches the certificate store to the new listener.
        settings.Publish(("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "yarp"));
        Assert.True(await Eventually(() => Accepts(httpsPort)));
        Assert.True(await Eventually(() => Task.FromResult(callbackRuns > runsAfterFirstAdd)));
        Assert.Equal("CN=proxy.test.invalid", await HandshakeSubject(httpsPort, "proxy.test.invalid"));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// (d) A request already being served on a listener that is going away. Observed: it completes — the
    /// endpoint stops accepting new connections, and the ones it has drain. So an operator turning the
    /// proxy off does not cut off the responses already in flight.
    /// </summary>
    [Fact]
    public async Task ARequestInFlight_SurvivesItsEndpointBeingUnbound() {
        var (managementPort, httpPort) = (FreePort(), FreePort());
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var settings = new ReloadableSettings(("Watchtower:Proxy:Enabled", "false"));
        var app = Host(
            settings,
            managementPort,
            [("Watchtower:Proxy:Yarp:HttpPort", httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             ("Watchtower:Proxy:Yarp:HttpsPort", "0")],
            chain: null,
            onHttpsCallback: null);
        await using var _ = app;
        app.MapGet("/slow", async () => {
            entered.TrySetResult();
            await release.Task;
            return "done";
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        settings.Publish(("Watchtower:Proxy:Enabled", "true"));
        Assert.True(await Eventually(() => Accepts(httpPort)));

        using var client = new HttpClient();
        var inflight = Get(client, $"http://127.0.0.1:{httpPort}/slow");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        settings.Publish(("Watchtower:Proxy:Enabled", "false"));
        Assert.True(await Eventually(async () => !await Accepts(httpPort)));

        release.SetResult();
        Assert.Equal("done", await inflight.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The other half of "configure unconditionally": handing Kestrel a section with no <c>Endpoints</c>
    /// block at all must not suppress the hosting URLs, or every development and Aspire run would bind
    /// nothing. It does not — <c>ASPNETCORE_URLS</c> applies exactly as it did.
    /// </summary>
    [Fact]
    public async Task WithNoProxyEndpoints_TheHostingUrlsStillBind() {
        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var section = ProxyIngressKestrelConfiguration.Build(builder.Configuration);
        builder.WebHost.ConfigureKestrel((_, kestrel) => kestrel.Configure(section, reloadOnChange: true));

        await using var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient();
        Assert.Equal("ok", await Get(client, $"http://127.0.0.1:{port}/"));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A host wired the way <c>Program.cs</c> wires one: the settings stand-in below the Kestrel
    /// management endpoint, the projected section, and the SNI callback on the named TLS endpoint.
    /// </summary>
    private static WebApplication Host(
        ReloadableSettings settings,
        int managementPort,
        (string Key, string? Value)[] extra,
        TestChain? chain,
        Action? onHttpsCallback) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        ((IConfigurationBuilder)builder.Configuration).Add(settings);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Kestrel:Endpoints:Http:Url", $"http://127.0.0.1:{managementPort}"),
            .. extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)),
        ]);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var section = ProxyIngressKestrelConfiguration.Build(builder.Configuration);
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel((_, kestrel) => {
            var loader = kestrel.Configure(section, reloadOnChange: true);
            if (chain is null) return;
            loader.Endpoint(ProxyIngressKestrelConfiguration.HttpsEndpointName, endpoint => {
                onHttpsCallback?.Invoke();
                endpoint.ListenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                endpoint.ListenOptions.UseHttps(new TlsHandshakeCallbackOptions {
                    OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions {
                        ServerCertificate = ServableCertificate(chain),
                    }),
                });
            });
        });

        var app = builder.Build();
        app.MapGet("/", () => "ok");
        return app;
    }

    /// <summary>
    /// The chain's leaf in a form SChannel can serve: <c>CreateFromPem</c> attaches the private key
    /// ephemerally, which Windows refuses for TLS — the handshake dies as an EOF on the client — so
    /// the pair goes through PKCS#12 exactly as the certificate store does.
    /// </summary>
    private static X509Certificate2 ServableCertificate(TestChain chain) {
        using var pem = X509Certificate2.CreateFromPem(chain.PemChain, chain.KeyPem);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }

    /// <summary>The subject of the certificate a TLS handshake against <paramref name="port"/> served.</summary>
    private static async Task<string> HandshakeSubject(int port, string sni) {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sni });
        return tls.RemoteCertificate!.Subject;
    }

    private static Task<string> Get(HttpClient client, string url) => client.GetStringAsync(url);

    private static async Task<bool> Eventually(Func<Task<bool>> condition) {
        for (var i = 0; i < 60; i++) {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<bool> Accepts(int port) {
        try {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        } catch {
            return false;
        }
    }

    private static int FreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
