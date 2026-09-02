using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Api;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.PortRoutes;
using Watchtower.Application.Services.Yarp;
using Watchtower.Application.Tests;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Watchtower.Api.Tests;

/// <summary>
/// Boots the real <see cref="Program"/> pipeline over a private PostgreSQL database
/// (<see cref="PostgresTestServer"/>). Exercising the actual host is the point of this project: the
/// middleware order, the endpoint gating and the cookie attributes are all properties of
/// <c>Program.cs</c> and its endpoint files, and a hand-rebuilt approximation of that wiring would
/// happily pass while the shipped one was wrong.
/// </summary>
/// <remarks>
/// Configuration reaches the app through <see cref="IWebHostBuilder.UseSetting"/>, which the deferred host
/// builder turns into command-line arguments for the entry point — the one channel that lands
/// <em>before</em> <c>Program.cs</c> reads <c>Watchtower:Auth:Enabled</c> off
/// <c>builder.Configuration</c>, which happens well before <c>builder.Build()</c>. The connection string
/// goes down the same channel, so the host resolves it exactly as a deployment would.
/// </remarks>
public sealed class WatchtowerApiFactory : WebApplicationFactory<Program> {
    private readonly string _connectionString;
    private readonly string _dataDirectory;
    private readonly (string Key, string? Value)[] _settings;

    public WatchtowerApiFactory(params (string Key, string? Value)[] settings) {
        _settings = settings;
        _dataDirectory = Path.Combine(Path.GetTempPath(), "watchtower-api-tests", Guid.NewGuid().ToString("N"));
        _connectionString = PostgresTestServer.CreateDatabase();
    }

    /// <summary>The bootstrap password the <c>admin</c> account is created with.</summary>
    public const string AdminPassword = "correct-horse-battery";

    /// <summary>
    /// The compose CLI the host runs with: a stub that records <c>down</c> requests and answers with
    /// <see cref="StubComposeCliService.DownExitCode"/> rather than starting a subprocess.
    /// </summary>
    /// <remarks>Held here rather than resolved from the container so a test can arm it before the first request.</remarks>
    public StubComposeCliService Compose { get; } = new();

    private readonly RecordingProxyProvider _proxy = new();

    /// <summary>
    /// The proxy provider the host runs with: a double that records reloads instead of reconciling a
    /// data plane. Provider-agnostic on purpose — it replaces whichever backend <c>Proxy:Provider</c>
    /// selects, so these tests do not move when the default provider does.
    /// </summary>
    /// <remarks>
    /// Held here rather than resolved from the container, like the compose stub above. Throws on a host
    /// built with <see cref="UseRealProxyProvider"/>, where there is nothing recording to read.
    /// </remarks>
    public RecordingProxyProvider Proxy => UseRealProxyProvider
        ? throw new InvalidOperationException(
            "This host runs the real proxy provider (UseRealProxyProvider); nothing is recording.")
        : _proxy;

    /// <summary>
    /// Extra registrations layered on top of the host's own, applied last — so a test can substitute a
    /// service this factory does not know about (the release-intake registry lookup, say) without every
    /// such seam having to become a property here. Set through an object initializer, so it is in place
    /// before the host is built.
    /// </summary>
    public Action<IServiceCollection>? AdditionalServices { get; init; }

    /// <summary>
    /// Opts out of the recording proxy provider and leaves the real router — and through it the real
    /// in-process provider — in place. For the tests that are <em>about</em> the in-process proxy: they
    /// project a real route table, bind a real listener state, and ask <c>proxy.getStatus</c> what the
    /// provider itself thinks. Set through an object initializer, so it is in place before the host is built.
    /// </summary>
    public bool UseRealProxyProvider { get; init; }

    /// <summary>The deploy queue the host runs with: accepts and records work without running it.</summary>
    public QueuedOnlyDeployQueueService DeployQueue =>
        (QueuedOnlyDeployQueueService)Services.GetRequiredService<DeployQueueService>();

    /// <summary>
    /// Opts out of the recording forwarder and runs the host with YARP's real one, for a test that stands a
    /// loopback upstream up and wants the bytes to actually travel. Set through an object initializer, so
    /// it is in place before the host is built.
    /// </summary>
    public bool UseRealForwarder { get; init; }

    /// <summary>
    /// The ACME transport the host runs with. Set to <see cref="FakeAcmeServer.Transport"/> to point
    /// issuance at an in-process CA; left null the host keeps the real one, which never gets used because
    /// no test enables the certificate manager's loop.
    /// </summary>
    public IAcmeTransportFactory? AcmeTransport { get; init; }

    /// <summary>
    /// The DNS resolver the host runs with: a stub that answers for everything unless a test says
    /// otherwise. Substituted for every test, like the compose CLI and the forwarder — a suite that
    /// queries the developer's real resolver is one that fails differently on a train.
    /// </summary>
    public StubDnsPreflight Dns { get; } = new();

    /// <summary>
    /// The <c>acme-issuer</c> role lease the host runs with (ADR-0024 decision 5): a stub that says this
    /// instance holds it. Set <c>IsHeld = false</c> to exercise the non-issuer half.
    /// </summary>
    public StubRoleLease IssuerLease { get; private set; } = new(CertificateManager.IssuerRole);

    /// <summary>The forwarder the host runs with: a double that records instead of connecting.</summary>
    /// <remarks>Throws when the host was built with <see cref="UseRealForwarder"/>.</remarks>
    public RecordingHttpForwarder Forwarder =>
        (RecordingHttpForwarder)Services.GetRequiredService<IHttpForwarder>();

    /// <summary>
    /// A host with the in-process proxy as the active provider. The two settings are what
    /// <see cref="YarpProxyProvider"/> gates on, so without them its route projection no-ops and the
    /// dispatcher sees an empty table.
    /// </summary>
    public static WatchtowerApiFactory WithYarpProxy(params (string Key, string? Value)[] settings) =>
        new([("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "yarp"), .. settings]) {
            UseRealProxyProvider = true,
        };

    /// <summary>The port a request is treated as having arrived on unless it says otherwise.</summary>
    public const int ManagementPort = 8080;

    /// <summary>
    /// A host with the in-process proxy <em>and</em> the shipped image's endpoint split: ingress on
    /// 8081/8443, management on 8080. The ports are not faked — the listener state derives them from the
    /// reverse-proxy settings exactly as it does in the container, and all this host adds is the
    /// management endpoint the image configures. <c>TestServer</c> does report no local port, so the client
    /// names one per request through <see cref="CreateApiClient(int?)"/>.
    /// </summary>
    public static WatchtowerApiFactory WithIngress(params (string Key, string? Value)[] settings) =>
        new([("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "yarp"), .. settings]) {
            UseRealProxyProvider = true,
            HasIngress = true,
        };

    /// <summary>
    /// Whether this host runs with the ingress/management split. False by default, which is the
    /// single-listener shape every other test in this assembly runs under — expressed the way an operator
    /// would express it, by turning both ingress ports off.
    /// </summary>
    public bool HasIngress { get; init; }

    /// <summary>
    /// Projects the seeded routes into the in-process proxy's routing table, the way a route change or the
    /// startup reconcile does. Explicit because the factory drops the hosted services, so nothing calls it
    /// on its own — seed the estate first, then apply.
    /// </summary>
    /// <remarks>
    /// Both halves, in the order <see cref="ProxyProviderRouter"/> drives them: the domain routes are the
    /// in-process provider's and the port routes are <see cref="PortRoutePlane"/>'s, which serves them
    /// under every provider (ADR-0033 addendum).
    /// </remarks>
    public async Task ApplyProxyAsync() {
        var ct = TestContext.Current.CancellationToken;
        await Services.GetRequiredService<YarpProxyProvider>().ApplyAsync(ct);
        await Services.GetRequiredService<PortRoutePlane>().ApplyAsync(ct);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        // Production, not Development: the development-only CORS policy would otherwise join the pipeline
        // and the tests would stop describing what ships.
        builder.UseEnvironment(Environments.Production);

        builder.UseSetting(WatchtowerConnectionString.ConfigurationKey, _connectionString);
        // The two legacy directories the one-shot file import reads (ADR-0024). Nothing else looks at
        // them any more; they are pointed at a scratch directory so no test can read the developer's
        // real /data.
        builder.UseSetting("Watchtower:Auth:KeyPath", Path.Combine(_dataDirectory, "auth-keys"));
        builder.UseSetting("Watchtower:Proxy:Yarp:CertPath", Path.Combine(_dataDirectory, "proxy-certs"));
        builder.UseSetting("Watchtower:Auth:BootstrapPassword", AdminPassword);

        // The listener facts, stated the way the container states them — the ingress endpoints are derived
        // from these, so a test host says what it wants rather than writing YarpListenerState by hand.
        // Both ports off is the single-listener shape; HasIngress is the shipped image's split.
        builder.UseSetting(
            "Watchtower:Proxy:Yarp:HttpPort",
            HasIngress ? YarpProxyOptions.DefaultHttpPort.ToString(CultureInfo.InvariantCulture) : "0");
        builder.UseSetting(
            "Watchtower:Proxy:Yarp:HttpsPort",
            HasIngress ? YarpProxyOptions.DefaultHttpsPort.ToString(CultureInfo.InvariantCulture) : "0");
        if (HasIngress)
            builder.UseSetting(
                "Kestrel:Endpoints:Http:Url",
                string.Create(CultureInfo.InvariantCulture, $"http://+:{ManagementPort}"));

        // Last, so a test that names one of the keys above wins over the defaults.
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);

        builder.ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        builder.ConfigureServices(services => {
            // Drop every background service except the auth bootstrap. The rest reconcile Docker, Caddy and
            // the CI runners; none of that exists here, so they would only add start-up latency and a wall
            // of connection failures to the test log. The bootstrap stays because the admin account it
            // creates is what the login tests sign in as.
            var hosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) &&
                            d.ImplementationType != typeof(AuthBootstrapService))
                .ToList();
            foreach (var descriptor in hosted) services.Remove(descriptor);

            // The three services that reach outside the process on a request path, replaced by doubles
            // that record instead. Dropping the hosted registrations above is not enough: a request can
            // still call any of them directly — enqueuing a deploy really does start a worker that
            // clones a repository and shells out to compose, on a thread that would then write to this
            // host's database behind the test's back. Substituting them is also what makes their effects
            // (a compose down, a proxy reload) observable at all, since both no-op without a daemon.
            //
            // This is unconditional, so it applies to every test in this assembly, not only the ones
            // that assert on the doubles. That is safe today because no other test drives a code path
            // that reaches any of the three, and it is the safer default regardless: the alternative is
            // a test suite that can shell out to the developer's real Docker daemon by accident. A test
            // that genuinely needs the real implementations has to opt out here first.
            services.RemoveAll<ComposeCliService>();
            services.AddSingleton<ComposeCliService>(Compose);
            // The proxy, at the interface every consumer injects rather than at one provider: the router
            // it replaces would otherwise resolve whichever backend Proxy:Provider names, and the two
            // container-based ones talk to Docker or the Cloudflare API on the very calls these tests
            // trigger. UseRealProxyProvider opts out, for the hosts that exist to exercise the in-process
            // provider itself; YarpProxyProvider stays registered concretely either way, because
            // ApplyProxyAsync above drives the real one deliberately.
            if (!UseRealProxyProvider) {
                services.RemoveAll<IProxyProvider>();
                services.AddSingleton<IProxyProvider>(_proxy);
            }
            services.RemoveAll<DeployQueueService>();
            services.AddSingleton<DeployQueueService>(
                sp => ActivatorUtilities.CreateInstance<QueuedOnlyDeployQueueService>(sp));

            // The fourth, and the same reasoning: the in-process proxy's forwarder is the one component on
            // a request path that opens a socket to somewhere else. Substituted for every test rather than
            // only the proxy ones, so a host whose route table is unexpectedly populated records the
            // attempt instead of dialling a container alias that does not resolve.
            // The fifth: DNS. The certificate issuer resolves a host before it opens an order, and the
            // Routes page probes one on demand — neither should reach a real resolver from a test.
            services.RemoveAll<DnsPreflight>();
            services.AddSingleton<DnsPreflight>(Dns);

            if (AcmeTransport is not null) {
                services.RemoveAll<IAcmeTransportFactory>();
                services.AddSingleton(AcmeTransport);
            }

            // The acme-issuer lease (ADR-0024 decision 5), always held. The real one is acquired by a
            // heartbeat hosted service, and every hosted service but the auth bootstrap is dropped above
            // — so without this every host here would be a non-issuer and the ACME suites would assert
            // on an instance that is deliberately doing nothing. Held is also the single-node truth,
            // which is what these tests are about; the gate itself has its own tests with a lease that
            // says no.
            IssuerLease = services.UseStubIssuerLease();

            // The per-request half of the split, which no configuration can supply: TestServer opens no
            // socket, so Connection.LocalPort is whatever the filter below puts there.
            if (HasIngress)
                services.AddSingleton<IStartupFilter>(new TestLocalPortStartupFilter());

            if (!UseRealForwarder) {
                services.RemoveAll<IHttpForwarder>();
                services.AddSingleton<IHttpForwarder>(new RecordingHttpForwarder());
            }

            // Last, so a test's own registration wins over everything above it.
            AdditionalServices?.Invoke(services);
        });
    }

    /// <summary>The header <see cref="CreateApiClient(int?)"/> names the connection's local port in.</summary>
    public const string LocalPortHeader = "X-Test-Local-Port";

    /// <summary>
    /// Puts <see cref="LocalPortHeader"/> onto <c>Connection.LocalPort</c>, at the very front of the
    /// pipeline. Test-only and registered only by this factory: production code reads the real connection,
    /// and there is no header in it that could reach this.
    /// </summary>
    /// <remarks>
    /// An <see cref="IStartupFilter"/> rather than a <c>Configure</c> hook because filters run ahead of the
    /// application's own middleware, which is where this has to be — the ACME responder and the host
    /// dispatcher are the first two things in the pipeline and both are what these tests are about.
    /// </remarks>
    private sealed class TestLocalPortStartupFilter : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, following) => {
                if (int.TryParse(context.Request.Headers[LocalPortHeader], out var port))
                    context.Connection.LocalPort = port;
                await following(context);
            });
            next(app);
        };
    }

    /// <summary>Runs <paramref name="action"/> against a scope of the running host's container.</summary>
    public async Task WithScopeAsync(Func<IServiceProvider, Task> action) {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    /// <summary>
    /// A client that keeps cookies but never follows redirects — every assertion here is about the exact
    /// status and headers of one response. <paramref name="localPort"/> makes every request it sends look
    /// like it arrived on that listener, for the tests that are about the ingress/management split.
    /// </summary>
    public HttpClient CreateApiClient(int? localPort = null) {
        // Naming a port on a host that has no ingress ports would set Connection.LocalPort and change
        // nothing, because the dispatcher's ingress rules are all gated on the set being non-empty — so the
        // test would pass while asserting the single-listener behaviour it thought it had opted out of.
        if (localPort is not null && !HasIngress)
            throw new InvalidOperationException(
                $"A local port is only meaningful on a host with ingress ports; build it with "
                + $"{nameof(WithIngress)}() or set {nameof(HasIngress)}.");

        var client = CreateClient(new WebApplicationFactoryClientOptions {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        if (localPort is { } port)
            client.DefaultRequestHeaders.Add(LocalPortHeader, port.ToString(CultureInfo.InvariantCulture));
        return client;
    }

    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (!disposing) return;
        PostgresTestServer.Drop(_connectionString);
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }
}
