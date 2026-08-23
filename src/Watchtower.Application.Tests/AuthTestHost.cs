using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// A Watchtower service provider wired the way the real host wires it
/// (<see cref="WatchtowerServiceCollectionExtensions.AddWatchtowerServices"/>), but pointed at a
/// private PostgreSQL database (<see cref="PostgresTestServer"/>) and a scratch data directory instead
/// of <c>/data</c>. Exercising the production registration is the point: a test that hand-registered
/// Identity would not notice the host drifting away from it.
/// </summary>
/// <remarks>
/// <see cref="Restart"/> hands the same database name <em>and the same data directory</em> to a second
/// provider, which is how "what happens on the next start" is tested — in a deployment both survive a
/// restart, so a second host that saw an empty database would be modelling a disaster rather than a
/// restart.
/// </remarks>
public sealed class AuthTestHost : IDisposable {
    private readonly string _connectionString;
    private readonly bool _ownsResources;
    private readonly ServiceProvider _provider;
    private readonly ServiceCollection _registrations;
    private readonly string _dataDirectory;
    private readonly TestTimeProvider _time;
    private readonly Action<IServiceCollection>? _configure;

    private AuthTestHost(
        string connectionString,
        bool ownsResources,
        string dataDirectory,
        TestTimeProvider time,
        Action<IServiceCollection>? configure,
        IEnumerable<KeyValuePair<string, string?>> settings) {
        _connectionString = connectionString;
        _ownsResources = ownsResources;
        _time = time;
        _configure = configure;
        _dataDirectory = dataDirectory;

        // KeyPath and the proxy cert path default to /data/*, which AddWatchtowerServices creates
        // eagerly — point them at a scratch directory so tests never write outside their temp space.
        // The connection string is passed the same way the deployment passes it, so the registration
        // under test is the shipped one rather than something the test rewires afterwards.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Concat([
                new KeyValuePair<string, string?>(
                    WatchtowerConnectionString.ConfigurationKey, _connectionString),
                new KeyValuePair<string, string?>("Watchtower:Auth:KeyPath", Path.Combine(_dataDirectory, "auth-keys")),
                new KeyValuePair<string, string?>("Watchtower:Proxy:Yarp:CertPath", Path.Combine(_dataDirectory, "proxy-certs")),
            ]))
            .Build();

        _registrations = [];
        _registrations.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        // Registered first so AddWatchtowerServices' TryAddSingleton(TimeProvider.System) stands down:
        // session expiry is a clock decision, and the tests have to be able to move the clock.
        _registrations.AddSingleton<TimeProvider>(_time);
        _registrations.AddWatchtowerServices(configuration);
        _configure?.Invoke(_registrations);

        _provider = _registrations.BuildServiceProvider();
    }

    public IServiceProvider Services => _provider;

    /// <summary>The host's clock. Moving it is how session expiry, sliding renewal and the absolute cap are exercised.</summary>
    public TestTimeProvider Time => _time;

    /// <summary>Starts a host over a brand-new database. <paramref name="settings"/> are <c>Watchtower:*</c> configuration keys.</summary>
    public static AuthTestHost Start(params (string Key, string? Value)[] settings) =>
        Start(configure: null, settings);

    /// <summary>
    /// Starts a host with extra registrations layered on top of the production ones — used to add a single
    /// generated handler pipeline so a test can dispatch through it without booting the whole Elarion host.
    /// </summary>
    public static AuthTestHost Start(Action<IServiceCollection>? configure, params (string Key, string? Value)[] settings) =>
        new(
            PostgresTestServer.CreateDatabase(),
            ownsResources: true,
            Path.Combine(Path.GetTempPath(), "watchtower-tests", Guid.NewGuid().ToString("N")),
            new TestTimeProvider(DateTimeOffset.UtcNow),
            configure,
            ToConfiguration(settings));

    /// <summary>
    /// Simulates the next process start against the same database and the same <c>/data</c> volume,
    /// optionally with different configuration.
    /// </summary>
    public AuthTestHost Restart(params (string Key, string? Value)[] settings) =>
        new(_connectionString, ownsResources: false, _dataDirectory, _time, _configure, ToConfiguration(settings));

    /// <summary>
    /// Builds the bootstrap hosted service the way the host would, first asserting that
    /// <c>AddWatchtowerServices</c> really registers it — <c>IHostedService</c> is not resolved
    /// directly because that would also construct the Docker- and proxy-facing background services.
    /// </summary>
    public AuthBootstrapService CreateBootstrapService() {
        Assert.Contains(_registrations, d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AuthBootstrapService));
        return ActivatorUtilities.CreateInstance<AuthBootstrapService>(_provider);
    }

    /// <summary>A user shaped for <c>UserManager.CreateAsync</c>, which overwrites the normalized name and stamp itself.</summary>
    public static User NewUser(string userName) => new() {
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        PasswordHash = string.Empty,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
    };

    public void Dispose() {
        _provider.Dispose();
        // A restarted host borrows both; only the host that created them cleans up, and it is disposed last.
        if (!_ownsResources) return;
        PostgresTestServer.Drop(_connectionString);
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }

    private static IEnumerable<KeyValuePair<string, string?>> ToConfiguration((string Key, string? Value)[] settings) =>
        settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value));
}
