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
/// listener whose Kestrel endpoint name did not exist when the process started, claimed from Kestrel's
/// <em>endpoint defaults</em> by its port and made TLS there, presenting Watchtower's own LAN
/// certificate.
/// </summary>
/// <remarks>
/// Every assertion here stands in for a thing that cannot be checked any other way. That a config-defined
/// <c>https://</c> endpoint with no <c>Certificate</c> section binds at all once the endpoint defaults
/// have made it TLS is a property of Kestrel's configuration loader; that ALPN still negotiates h2 on it
/// is a property of the callback path. The rest are the ones the first attempt at this got wrong:
/// <em>other</em> HTTPS endpoints — an operator's own certificate, the default certificate, none at all —
/// have to behave exactly as they did before port routes existed, and the only way to know that is to
/// stand them up next to a live port route and dial them.
/// <para>
/// One limit worth stating: the internal chain is two certificates deep, so what reaches the wire is the
/// leaf either way, and supplying a whole <c>SslStreamCertificateContext</c> rather than a bare leaf —
/// which is what keeps <c>SslStream</c> from rebuilding a chain per handshake out of the machine's trust
/// store — is not separately observable here. The assertions are on the outcome.
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
        // own callback, exactly as the port listeners do — which is what keeps the routed domains being
        // served by the SNI store rather than by the LAN certificate.
        var named = await Handshake(httpsPort, "proxy.test.invalid");
        Assert.Equal("CN=proxy.test.invalid", named.Subject);

        // (d) The route is deleted. The listener goes with it, and the two that were always there stay.
        // Retried: adding and removing an endpoint makes the loader rebind, and a listener being rebuilt
        // refuses a connection or two on the way through.
        settings.Publish(("Watchtower:Proxy:Enabled", "true"));
        Assert.True(await Eventually(async () => !await Accepts(routePort)));
        Assert.True(await Eventually(() => Accepts(managementPort)));
        Assert.True(await Eventually(() => Accepts(httpsPort)));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An endpoint of the operator's own, with a certificate of its own, while a port route is active.
    /// It keeps serving that certificate — which is the property the mechanism was chosen for.
    /// </summary>
    /// <remarks>
    /// The rejected alternative was a <c>ServerCertificateSelector</c> on the HTTPS <em>defaults</em>.
    /// That is applied to every HTTPS listener in the process, and Kestrel discards a configured
    /// <c>ServerCertificate</c> when a selector is present — so this endpoint would have stopped serving
    /// at the next rebind, silently, on a deployment whose only change was adding a port route. Claiming
    /// individual listeners by port cannot reach a port no route owns.
    /// </remarks>
    [Fact]
    public async Task AnOperatorsHttpsEndpoint_KeepsItsOwnCertificate() {
        var (managementPort, routePort, ownPort) = (FreePort(), FreePort(), FreePort());
        using var ca = InternalCa.Issue("nas.lan", IPAddress.Loopback);
        using var own = TestCertificates.Create("own.test.invalid");
        using var files = new CertificateFiles(own);

        var settings = new ReloadableSettings(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Yarp:PortRoutePorts", Text(routePort)));
        var app = Host(
            settings,
            managementPort,
            [
                ("Watchtower:Proxy:Yarp:HttpPort", "0"),
                ("Watchtower:Proxy:Yarp:HttpsPort", "0"),
                ("Kestrel:Endpoints:Own:Url", $"https://127.0.0.1:{Text(ownPort)}"),
                ("Kestrel:Endpoints:Own:Certificate:Path", files.CertificatePath),
                ("Kestrel:Endpoints:Own:Certificate:KeyPath", files.KeyPath),
            ],
            ca.Context,
            sni: null);
        await using var _ = app;
        await app.StartAsync(TestContext.Current.CancellationToken);

        var theirs = await Eventually(() => Handshake(ownPort, "own.test.invalid"));
        Assert.Equal("CN=own.test.invalid", theirs.Subject);

        // …and the port route is serving its own material at the same time, from the same process.
        var ours = await Eventually(() => Handshake(routePort, "nas.lan"));
        Assert.Equal($"CN={InternalCaNames.SharedLeafHost}", ours.Subject);
        Assert.True(ChainsTo(ours.Certificate, ca.Root));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The same property one level down: an endpoint with no <c>Certificate</c> section of its own, being
    /// served by <c>Kestrel:Certificates:Default</c>. A selector on the HTTPS defaults would have skipped
    /// that fallback entirely — <c>HasServerCertificateOrSelector</c> is true once one is installed — so
    /// this endpoint would have stopped serving too.
    /// </summary>
    [Fact]
    public async Task AnHttpsEndpointOnTheDefaultCertificate_KeepsServing() {
        var (managementPort, routePort, fallbackPort) = (FreePort(), FreePort(), FreePort());
        using var ca = InternalCa.Issue("nas.lan", IPAddress.Loopback);
        using var fallback = TestCertificates.Create("default.test.invalid");
        using var files = new CertificateFiles(fallback);

        var settings = new ReloadableSettings(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Yarp:PortRoutePorts", Text(routePort)));
        var app = Host(
            settings,
            managementPort,
            [
                ("Watchtower:Proxy:Yarp:HttpPort", "0"),
                ("Watchtower:Proxy:Yarp:HttpsPort", "0"),
                ("Kestrel:Endpoints:Fallback:Url", $"https://127.0.0.1:{Text(fallbackPort)}"),
                ("Kestrel:Certificates:Default:Path", files.CertificatePath),
                ("Kestrel:Certificates:Default:KeyPath", files.KeyPath),
            ],
            ca.Context,
            sni: null);
        await using var _ = app;
        await app.StartAsync(TestContext.Current.CancellationToken);

        var theirs = await Eventually(() => Handshake(fallbackPort, "default.test.invalid"));
        Assert.Equal("CN=default.test.invalid", theirs.Subject);

        var ours = await Eventually(() => Handshake(routePort, "nas.lan"));
        Assert.Equal($"CN={InternalCaNames.SharedLeafHost}", ours.Subject);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An <c>https://</c> endpoint with no certificate configured anywhere, while a port route is active:
    /// whatever Kestrel would have done with it before port routes existed, it still does. It is never
    /// handed the LAN leaf, because our hook claims a listener only when a route owns its port.
    /// </summary>
    /// <remarks>
    /// What Kestrel does here is the machine's business, not ours, which is why the assertion is on the
    /// invariance rather than on an outcome. On a developer machine with the ASP.NET Core development
    /// certificate installed the endpoint binds and serves <c>CN=localhost</c>; on a container that has
    /// none, the host fails to start with "no server certificate was specified" — the behaviour that was
    /// always there, and the one worth stating in the ADR. The rejected mechanism changed it into a third
    /// thing: a host that starts and then refuses every handshake on a listener the operator believes is
    /// configured.
    /// </remarks>
    [Fact]
    public async Task AnHttpsEndpointWithNoCertificateOfItsOwn_IsNeverGivenTheLanLeaf() {
        var (managementPort, routePort, barePort) = (FreePort(), FreePort(), FreePort());
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
                ("Kestrel:Endpoints:Bare:Url", $"https://127.0.0.1:{Text(barePort)}"),
            ],
            ca.Context,
            sni: null);
        await using var _ = app;

        try {
            await app.StartAsync(TestContext.Current.CancellationToken);
        } catch (InvalidOperationException ex) {
            // The no-development-certificate machine: unchanged from before the feature, and there is
            // nothing further to observe.
            Assert.Contains("certificate", ex.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        // The machine has a development certificate, so the endpoint binds — on whatever Kestrel picked,
        // which is emphatically not ours.
        var bare = await Eventually(() => Handshake(barePort, "localhost"));
        Assert.NotEqual($"CN={InternalCaNames.SharedLeafHost}", bare.Subject);
        Assert.False(ChainsTo(bare.Certificate, ca.Root));

        var ours = await Eventually(() => Handshake(routePort, "nas.lan"));
        Assert.Equal($"CN={InternalCaNames.SharedLeafHost}", ours.Subject);

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
    /// Registering here means the port set is already current when Kestrel creates the listener that
    /// reads it.
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
                () => PortRouteListeners.BoundPorts(section),
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

    /// <summary>
    /// A generated chain on disk in the PEM pair form <c>Kestrel:Certificates:*</c> reads. Written to a
    /// scratch directory rather than checked in, for the reason <see cref="TestCertificates"/> states.
    /// </summary>
    private sealed class CertificateFiles : IDisposable {
        private readonly string _directory;

        public CertificateFiles(TestChain chain) {
            ArgumentNullException.ThrowIfNull(chain);
            _directory = Path.Combine(Path.GetTempPath(), $"wt-cert-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            CertificatePath = Path.Combine(_directory, "cert.pem");
            KeyPath = Path.Combine(_directory, "key.pem");
            File.WriteAllText(CertificatePath, chain.PemChain);
            File.WriteAllText(KeyPath, chain.KeyPem);
        }

        public string CertificatePath { get; }
        public string KeyPath { get; }

        public void Dispose() {
            try {
                Directory.Delete(_directory, recursive: true);
            } catch (IOException) {
                // A scratch directory that outlives the run is not worth failing a test over.
            }
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
