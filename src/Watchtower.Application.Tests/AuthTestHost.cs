using System.Reflection;
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
/// restart. A second live host over the same database is also how the cross-instance behaviour is
/// tested: two providers with two settings-change listeners against one PostgreSQL is exactly the
/// two-node shape.
/// </remarks>
public sealed class AuthTestHost : IDisposable {
    private readonly string _connectionString;
    private readonly bool _ownsResources;
    private readonly ServiceProvider _provider;
    private readonly ServiceCollection _registrations;
    private readonly string _dataDirectory;
    private readonly TestTimeProvider _time;
    private readonly Action<IServiceCollection>? _configure;

    /// <summary>Hosted services this test asked for by name, stopped again on dispose.</summary>
    private readonly List<IHostedService> _started = [];

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

        // The connection string is passed the same way the deployment passes it, so the registration
        // under test is the shipped one rather than something the test rewires afterwards. Since
        // ADR-0024 there are no key or certificate directories to redirect: both live in the database,
        // and the scratch directory below is only what FileStateImport is pointed at.
        // The two legacy paths are still configuration keys, read by FileStateImport alone. Defaulted to
        // a scratch directory so no test can ever read the developer's real /data — and only defaulted,
        // so a test that lays out an old volume of its own can point them at it.
        var values = settings.ToList();
        values.Add(new KeyValuePair<string, string?>(
            WatchtowerConnectionString.ConfigurationKey, _connectionString));
        Default(values, "Watchtower:Auth:KeyPath", Path.Combine(_dataDirectory, "auth-keys"));
        Default(values, "Watchtower:Proxy:Yarp:CertPath", Path.Combine(_dataDirectory, "proxy-certs"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        _registrations = [];
        _registrations.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        // The host builder registers this and the application layer resolves it (FileStateImport reads
        // the two removed legacy paths off it); a bare ServiceCollection has to say so itself.
        _registrations.AddSingleton<IConfiguration>(configuration);
        // Registered first so AddWatchtowerServices' TryAddSingleton(TimeProvider.System) stands down:
        // session expiry is a clock decision, and the tests have to be able to move the clock.
        _registrations.AddSingleton<TimeProvider>(_time);
        _registrations.AddWatchtowerServices(configuration);
        _configure?.Invoke(_registrations);

        _provider = _registrations.BuildServiceProvider();

        // The same step the host runs after migrating (Program.InitializeDatabaseAsync): the legacy file
        // import, the certificate store's first load and the signing key. Done here so a test is never
        // exercising a startup shape the deployment does not have — and so the proxy/auth services are
        // usable the moment Start() returns, exactly as they are the moment the host serves.
        _provider.InitializeWatchtowerStateAsync().GetAwaiter().GetResult();
    }

    public IServiceProvider Services => _provider;

    /// <summary>The host's clock. Moving it is how session expiry, sliding renewal and the absolute cap are exercised.</summary>
    public TestTimeProvider Time => _time;

    /// <summary>
    /// Starts the PostgreSQL <c>LISTEN</c> loop behind the settings change source — the one hosted
    /// service a cross-instance test cannot do without, since nothing observes another node's writes
    /// until something is listening.
    /// </summary>
    /// <remarks>
    /// Started by name rather than by resolving <c>IEnumerable&lt;IHostedService&gt;</c>, which would
    /// also construct the Docker-, proxy- and CI-facing background services this host deliberately never
    /// runs. The instance is a second one alongside the container's (which is never started), and that is
    /// harmless: both drive the same singleton change source, which is where the watches live.
    /// </remarks>
    public async Task StartSettingsChangeListenerAsync(CancellationToken ct = default) {
        var descriptor = _registrations.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType?.FullName
            == "Elarion.Settings.PostgreSql.PostgreSqlSettingsChangeListener");
        if (descriptor?.ImplementationType is null)
            throw new InvalidOperationException(
                "AddWatchtowerServices no longer registers the PostgreSQL settings change listener.");

        var listener = (IHostedService)ActivatorUtilities.CreateInstance(
            _provider, descriptor.ImplementationType);
        await listener.StartAsync(ct);
        _started.Add(listener);

        // The loop connects asynchronously, and a notification sent before it has issued its LISTEN is
        // simply lost — PostgreSQL does not queue for absent listeners. Waiting for the first
        // establishment is the difference between a cross-instance test and a flaky one.
        var listening = descriptor.ImplementationType
            .GetProperty("Listening", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(listener) as Task;
        if (listening is not null) await listening.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

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
        foreach (var service in _started)
            try {
                service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            } catch (Exception) {
                // Shutting down; a listener that will not stop must not fail the test that started it.
            }
        _started.Clear();
        _provider.Dispose();
        // A restarted host borrows both; only the host that created them cleans up, and it is disposed last.
        if (!_ownsResources) return;
        PostgresTestServer.Drop(_connectionString);
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }

    private static IEnumerable<KeyValuePair<string, string?>> ToConfiguration((string Key, string? Value)[] settings) =>
        settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value));

    /// <summary>Adds a value only when the test did not supply one — a memory source rejects duplicates.</summary>
    private static void Default(List<KeyValuePair<string, string?>> values, string key, string value) {
        if (values.Any(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))) return;
        values.Add(new KeyValuePair<string, string?>(key, value));
    }
}
