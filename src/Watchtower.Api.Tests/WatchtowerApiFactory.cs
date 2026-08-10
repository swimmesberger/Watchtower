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

    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        // Production, not Development: the development-only CORS policy would otherwise join the pipeline
        // and the tests would stop describing what ships.
        builder.UseEnvironment(Environments.Production);

        // DbPath and Auth:KeyPath default to /data/*, which AddWatchtowerServices creates eagerly.
        builder.UseSetting("Watchtower:DbPath", Path.Combine(_dataDirectory, "watchtower.db"));
        builder.UseSetting("Watchtower:Auth:KeyPath", Path.Combine(_dataDirectory, "auth-keys"));
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
