using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Api;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Watchtower.Api.Tests;

/// <summary>
/// Boots the real <see cref="Program"/> pipeline over a private in-memory SQLite database. Exercising the
/// actual host is the point of this project: the middleware order, the endpoint gating and the cookie
/// attributes are all properties of <c>Program.cs</c> and its endpoint files, and a hand-rebuilt
/// approximation of that wiring would happily pass while the shipped one was wrong.
/// </summary>
/// <remarks>
/// Configuration reaches the app through <see cref="IWebHostBuilder.UseSetting"/>, which the deferred host
/// builder turns into command-line arguments for the entry point — the one channel that lands
/// <em>before</em> <c>Program.cs</c> reads <c>Watchtower:Auth:Enabled</c> off
/// <c>builder.Configuration</c>, which happens well before <c>builder.Build()</c>.
/// The connection is held open for the factory's lifetime because closing the last connection to
/// <c>DataSource=:memory:</c> discards the database.
/// </remarks>
public sealed class WatchtowerApiFactory : WebApplicationFactory<Program> {
    private readonly SqliteConnection _connection;
    private readonly string _dataDirectory;
    private readonly (string Key, string? Value)[] _settings;

    public WatchtowerApiFactory(params (string Key, string? Value)[] settings) {
        _settings = settings;
        _dataDirectory = Path.Combine(Path.GetTempPath(), "watchtower-api-tests", Guid.NewGuid().ToString("N"));
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    /// <summary>The bootstrap password the <c>admin</c> account is created with.</summary>
    public const string AdminPassword = "correct-horse-battery";

    /// <summary>
    /// The compose CLI the host runs with: a stub that records <c>down</c> requests and answers with
    /// <see cref="StubComposeCliService.DownExitCode"/> rather than starting a subprocess.
    /// </summary>
    /// <remarks>Held here rather than resolved from the container so a test can arm it before the first request.</remarks>
    public StubComposeCliService Compose { get; } = new();

    /// <summary>The Caddy manager the host runs with: a double that counts config reloads.</summary>
    public RecordingCaddyManager Caddy => (RecordingCaddyManager)Services.GetRequiredService<CaddyManager>();

    /// <summary>The deploy queue the host runs with: accepts and records work without running it.</summary>
    public QueuedOnlyDeployQueueService DeployQueue =>
        (QueuedOnlyDeployQueueService)Services.GetRequiredService<DeployQueueService>();

    /// <summary>
    /// Opts out of the recording forwarder and runs the host with YARP's real one, for a test that stands a
    /// loopback upstream up and wants the bytes to actually travel. Set through an object initializer, so
    /// it is in place before the host is built.
    /// </summary>
    public bool UseRealForwarder { get; init; }

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
        new([("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "yarp"), .. settings]);

    /// <summary>
    /// Projects the seeded routes into the in-process proxy's routing table, the way a route change or the
    /// startup reconcile does. Explicit because the factory drops the hosted services, so nothing calls it
    /// on its own — seed the estate first, then apply.
    /// </summary>
    public Task ApplyProxyAsync() =>
        Services.GetRequiredService<YarpProxyProvider>().ApplyAsync(TestContext.Current.CancellationToken);

    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        // Production, not Development: the development-only CORS policy would otherwise join the pipeline
        // and the tests would stop describing what ships.
        builder.UseEnvironment(Environments.Production);

        // DbPath, Auth:KeyPath and the proxy cert path default to /data/*, which AddWatchtowerServices
        // creates eagerly.
        builder.UseSetting("Watchtower:DbPath", Path.Combine(_dataDirectory, "watchtower.db"));
        builder.UseSetting("Watchtower:Auth:KeyPath", Path.Combine(_dataDirectory, "auth-keys"));
        builder.UseSetting("Watchtower:Proxy:Yarp:CertPath", Path.Combine(_dataDirectory, "proxy-certs"));
        builder.UseSetting("Watchtower:Auth:BootstrapPassword", AdminPassword);
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);

        builder.ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        builder.ConfigureServices(services => {
            // Swap the file-backed context for the shared in-memory connection. AddDbContext registers its
            // options with TryAdd, so the original descriptor has to go first.
            services.RemoveAll<DbContextOptions<WatchtowerDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<WatchtowerDbContext>(o =>
                o.UseSqlite(_connection).UseSnakeCaseNamingConvention());

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
            // clones a repository and shells out to compose, on a thread that would then race this
            // host's single shared SQLite connection. Substituting them is also what makes their effects
            // (a compose down, a proxy reload) observable at all, since both no-op without a daemon.
            //
            // This is unconditional, so it applies to every test in this assembly, not only the ones
            // that assert on the doubles. That is safe today because no other test drives a code path
            // that reaches any of the three, and it is the safer default regardless: the alternative is
            // a test suite that can shell out to the developer's real Docker daemon by accident. A test
            // that genuinely needs the real implementations has to opt out here first.
            services.RemoveAll<ComposeCliService>();
            services.AddSingleton<ComposeCliService>(Compose);
            services.RemoveAll<CaddyManager>();
            services.AddSingleton<CaddyManager>(sp => ActivatorUtilities.CreateInstance<RecordingCaddyManager>(sp));
            services.RemoveAll<DeployQueueService>();
            services.AddSingleton<DeployQueueService>(
                sp => ActivatorUtilities.CreateInstance<QueuedOnlyDeployQueueService>(sp));

            // The fourth, and the same reasoning: the in-process proxy's forwarder is the one component on
            // a request path that opens a socket to somewhere else. Substituted for every test rather than
            // only the proxy ones, so a host whose route table is unexpectedly populated records the
            // attempt instead of dialling a container alias that does not resolve.
            if (UseRealForwarder) return;
            services.RemoveAll<IHttpForwarder>();
            services.AddSingleton<IHttpForwarder>(new RecordingHttpForwarder());
        });
    }

    /// <summary>Runs <paramref name="action"/> against a scope of the running host's container.</summary>
    public async Task WithScopeAsync(Func<IServiceProvider, Task> action) {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    /// <summary>
    /// A client that keeps cookies but never follows redirects — every assertion here is about the exact
    /// status and headers of one response.
    /// </summary>
    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (!disposing) return;
        _connection.Dispose();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }
}
