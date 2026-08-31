using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Api.Proxy;
using Watchtower.Application.Services.InternalCa;
using Watchtower.Application.Services.Yarp;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The mechanism port-bound routes rest on (ADR-0033), proven against a real Kestrel on real sockets: a
/// listener whose Kestrel endpoint name did not exist when the process started, made TLS by the HTTPS
/// <em>defaults</em> rather than by a callback registered against its name, presenting Watchtower's own
/// LAN certificate because of the port the connection arrived on.
/// </summary>
/// <remarks>
/// Every assertion here stands in for a thing that cannot be checked any other way. That a config-defined
/// <c>https://</c> endpoint with no <c>Certificate</c> section binds at all is a property of Kestrel's
/// configuration loader; that the selector can see which listener a connection arrived on is a property of
/// <c>ConnectionContext.LocalEndPoint</c>; that the chain a client receives is the one we assembled rather
/// than one <c>SslStream</c> built out of the machine's trust store is a property of
/// <c>OnAuthenticate</c>. The alternative to this test is finding out on an operator's NAS.
/// <para>
/// One limit worth stating: the internal chain is two certificates deep, so what reaches the wire is the
/// leaf either way and <c>OnAuthenticate</c>'s contribution — handing <c>SslStream</c> the chain we
/// already assembled instead of making it build one per handshake out of the machine's trust store — is
/// not separately observable here. The assertions are on the outcome.
/// </para>
/// <para>
/// Ports are taken by opening and closing a <see cref="TcpListener"/> on 0, exactly as
/// <see cref="ProxyIngressEndpointReloadTests"/> does and with the same accepted race: a projected
/// endpoint has to name its port in configuration, so it cannot ask the OS for one.
/// </para>
/// </remarks>
public sealed class ProxyPortListenerReloadTests {
    /// <summary>
    /// The whole mechanism in one pass: the setting flips, a listener that no code named appears, serves
    /// a real handshake off the internal CA, negotiates h2 — and goes away again when the routes do.
    /// </summary>
    [Fact]
    public async Task APortRouteListener_FollowsTheSettingItsPortIsWrittenTo() {
        var (managementPort, httpsPort, routePort) = (FreePort(), FreePort(), FreePort());
        using var ca = InternalCa.Issue("nas.lan", IPAddress.Loopback);
        using var sni = TestCertificates.Create("proxy.test.invalid");

        var settings = new ReloadableSettings(("Watchtower:Proxy:Enabled", "true"));
        var app = Host(
            settings,
            managementPort,
            [
                ("Watchtower:Proxy:Yarp:HttpPort", "0"),
                ("Watchtower:Proxy:Yarp:HttpsPort", Text(httpsPort)),
            ],
            ca.Context,
            sni);
        await using var _ = app;
        await app.StartAsync(TestContext.Current.CancellationToken);

        // (a) No port routes yet: the management endpoint and the named TLS ingress endpoint are all there
        // is, and nothing is listening on the port a route is about to claim.
        Assert.True(await Accepts(managementPort));
        Assert.True(await Accepts(httpsPort));
        Assert.False(await Accepts(routePort));

        // (b) A port route is created, which is the only thing that writes this setting. The listener
        // appears with no restart and under a Kestrel endpoint name — ProxyPort{n} — that did not exist
        // when the server was configured.
        settings.Publish(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Yarp:PortRoutePorts", Text(routePort)));
        Assert.True(await Eventually(() => Accepts(routePort)));

        // …and it is Watchtower's own certificate that is served there, with the chain we assembled: the
        // presented material validates against the generated root and nothing else.
        var handshake = await Eventually(() => Handshake(routePort, "nas.lan"));
        Assert.Equal($"CN={InternalCaNames.SharedLeafHost}", handshake.Subject);
        Assert.True(ChainsTo(handshake.Certificate, ca.Root));
        // h2, because the listener fronts an arbitrary web application and a browser will ask for it.
        Assert.Equal(SslApplicationProtocol.Http2, handshake.Protocol);

        // (c) The named TLS ingress endpoint is untouched by any of it. It makes its listener TLS in its
        // own callback, so the HTTPS defaults the port routes installed never reach it — which is what
        // keeps the routed domains being served by the SNI store rather than by the LAN certificate.
        var named = await Handshake(httpsPort, "proxy.test.invalid");
        Assert.Equal("CN=proxy.test.invalid", named.Subject);

        // (d) The route is deleted. The listener goes with it, and the two that were always there stay.
        settings.Publish(("Watchtower:Proxy:Enabled", "true"));
        Assert.True(await Eventually(async () => !await Accepts(routePort)));
        Assert.True(await Accepts(managementPort));
        Assert.True(await Accepts(httpsPort));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The cost of putting a certificate selector on the HTTPS defaults: it is consulted for every
    /// <c>https://</c> endpoint, including one an operator configured themselves. The failure that buys
    /// has to be per connection — one refused handshake on that endpoint — and not a host that will not
    /// start, which is what a missing certificate used to mean.
    /// </summary>
    [Fact]
    public async Task AnHttpsEndpointOnAnUnknownPort_FailsItsOwnHandshakeAndNothingElse() {
        var (managementPort, routePort, strangePort) = (FreePort(), FreePort(), FreePort());
        using var ca = InternalCa.Issue("nas.lan", IPAddress.Loopback);

        var settings = new ReloadableSettings(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Yarp:PortRoutePorts", Text(routePort)));
        var app = Host(
            settings,
            managementPort,
            [
                ("Watchtower:Proxy:Yarp:HttpPort", "0"),
                ("Watchtower:Proxy:Yarp:HttpsPort", "0"),
                // An endpoint of the operator's own, with no certificate of its own either. It passes
                // through the projection untouched, which is the point.
                ("Kestrel:Endpoints:Extra:Url", $"https://127.0.0.1:{Text(strangePort)}"),
            ],
            ca.Context,
            sni: null);
        await using var _ = app;

        // The host starts. Before the selector existed this configuration was a startup failure.
        await app.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(await Eventually(() => Accepts(routePort)));
        Assert.True(await Accepts(strangePort));

        // The stranger's port answers the TCP connection and then refuses the handshake — the selector
        // holds nothing for a port no route is bound to.
        await Assert.ThrowsAnyAsync<Exception>(() => Handshake(strangePort, "whatever.invalid"));

        // …and the port route is serving throughout, which is the half that matters.
        var handshake = await Eventually(() => Handshake(routePort, "nas.lan"));
        Assert.True(ChainsTo(handshake.Certificate, ca.Root));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A host wired the way <c>Program.cs</c> wires one: the settings stand-in under the projection, the
    /// port-route TLS defaults, the loader, and — when a chain is given — the named SNI endpoint.
    /// </summary>
    /// <remarks>
    /// <see cref="ProxyListenerStateInitializer.Register"/> is called before the server is started, as it
    /// is in the host, and that ordering is load-bearing rather than cosmetic: both it and Kestrel's
    /// loader hang off the projected section's reload token, and whichever subscribed first runs first.
    /// Registering here means the port set the selector reads is already current when the listener
    /// Kestrel is about to bind starts accepting.
    /// </remarks>
    private static WebApplication Host(
        ReloadableSettings settings,
        int managementPort,
        (string Key, string? Value)[] extra,
        SslStreamCertificateContext lanCertificate,
        TestChain? sni) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        ((IConfigurationBuilder)builder.Configuration).Add(settings);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Kestrel:Endpoints:Http:Url", $"http://127.0.0.1:{Text(managementPort)}"),
            .. extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)),
        ]);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<YarpListenerState>();
        builder.Services.AddSingleton<ProxyIngressWarnings>();

        var section = ProxyIngressKestrelConfiguration.Build(builder.Configuration);
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel((_, kestrel) => {
            ProxyHttpsEndpoint.ConfigurePortRouteTls(
                kestrel,
                section,
                () => kestrel.ApplicationServices.GetRequiredService<YarpListenerState>().PortRoutePorts,
                () => lanCertificate,
                () => NullLogger.Instance);

            var loader = kestrel.Configure(section, reloadOnChange: true);
            if (sni is null) return;
            loader.Endpoint(ProxyIngressKestrelConfiguration.HttpsEndpointName, endpoint => {
                endpoint.ListenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                endpoint.ListenOptions.UseHttps(new TlsHandshakeCallbackOptions {
                    OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions {
                        ServerCertificate = X509Certificate2.CreateFromPem(sni.PemChain, sni.KeyPem),
                    }),
                });
            });
        });

        var app = builder.Build();
        app.MapGet("/", () => "ok");
        ProxyListenerStateInitializer.Register(app, section);
        return app;
    }

    /// <summary>What a TLS handshake against a port produced.</summary>
    private sealed record Presented(
        string Subject, X509Certificate2 Certificate, SslApplicationProtocol Protocol);

    /// <summary>
    /// Dials a port and completes a handshake, offering h2 first. Validation is accepted unconditionally
    /// here and applied afterwards against the root the test generated — asserting on the chain is the
    /// point, and a callback that returned false would only report "it failed".
    /// </summary>
    private static async Task<Presented> Handshake(int port, string sni) {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(10));
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = sni,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
        }).WaitAsync(TimeSpan.FromSeconds(10));
        var remote = tls.RemoteCertificate
            ?? throw new InvalidOperationException("The handshake produced no server certificate.");
        return new Presented(
            remote.Subject, X509CertificateLoader.LoadCertificate(remote.Export(X509ContentType.Cert)),
            tls.NegotiatedApplicationProtocol);
    }

    /// <summary>
    /// Whether the presented certificate validates under <paramref name="root"/> and no other trust. The
    /// machine's own store is deliberately out of the picture: a LAN client trusts exactly the root it
    /// imported, and a chain that only builds because the build machine trusts something is not one.
    /// </summary>
    private static bool ChainsTo(X509Certificate2 presented, X509Certificate2 root) {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(presented);
    }

    /// <summary>
    /// A root and a leaf from the production issuer, in the form the certificate store hands Kestrel.
    /// Generated in process rather than read from the store: the store needs a database, and what is
    /// under test here is the listener, not the storage.
    /// </summary>
    private sealed class InternalCa : IDisposable {
        private InternalCa(X509Certificate2 root, X509Certificate2 leaf, SslStreamCertificateContext context) {
            Root = root;
            _leaf = leaf;
            Context = context;
        }

        public X509Certificate2 Root { get; }
        public SslStreamCertificateContext Context { get; }
        private readonly X509Certificate2 _leaf;

        public static InternalCa Issue(string dnsName, IPAddress ip) {
            var now = DateTimeOffset.UtcNow;
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                "CN=Watchtower Internal CA (test)", rootKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
            var root = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

            try {
                using var issued = InternalCaIssuer.IssueLeaf(root, [dnsName], [ip], now);
                // Through PKCS#12, for the reason CertificateStore states: a key merely attached with
                // CopyWithPrivateKey is not usable for TLS on every platform.
                using var withKey = issued.Certificate.CopyWithPrivateKey(issued.Key);
                var pfx = withKey.Export(X509ContentType.Pkcs12);
                X509Certificate2 leaf;
                try {
                    leaf = X509CertificateLoader.LoadPkcs12(pfx, password: null);
                } finally {
                    CryptographicOperations.ZeroMemory(pfx);
                }
                return new InternalCa(root, leaf, SslStreamCertificateContext.Create(leaf, [], offline: true));
            } catch {
                root.Dispose();
                throw;
            }
        }

        public void Dispose() {
            Root.Dispose();
            _leaf.Dispose();
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<bool> Eventually(Func<Task<bool>> condition) {
        for (var i = 0; i < 60; i++) {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>
    /// Retries a handshake until one completes. A listener that has just been added is bound a moment
    /// before it can be dialled, and this is the only place a fixed delay would otherwise creep in.
    /// </summary>
    private static async Task<Presented> Eventually(Func<Task<Presented>> handshake) {
        for (var i = 0; i < 59; i++) {
            try {
                return await handshake();
            } catch {
                await Task.Delay(100);
            }
        }
        return await handshake();
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
