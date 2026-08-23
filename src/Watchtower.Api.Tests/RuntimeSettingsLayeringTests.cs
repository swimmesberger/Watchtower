using Elarion.Settings;
using Elarion.Settings.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Npgsql;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Pins the configuration precedence contract behind the runtime settings work: environment variables
/// are infrastructure-as-code pins and must win over the settings store, while the store still wins
/// over appsettings defaults and live-reloads. Also covers the synchronous boot snapshot that makes
/// stored values visible to the pre-DI reads in <c>Program.cs</c> (<c>Auth:Enabled</c>), which run
/// before the live settings provider has loaded anything.
/// </summary>
public sealed class RuntimeSettingsLayeringTests : IDisposable {
    private const string Key = "Watchtower:LayeringProbe";
    private const string EnvPrefix = "WT_LAYERING_TEST_";

    public RuntimeSettingsLayeringTests() =>
        Environment.SetEnvironmentVariable(EnvPrefix + "Watchtower__LayeringProbe", "from-env");

    public void Dispose() =>
        Environment.SetEnvironmentVariable(EnvPrefix + "Watchtower__LayeringProbe", null);

    private static ConfigurationManager BuildLikeTheHost(out SettingsConfigurationSource settingsSource) {
        var configuration = new ConfigurationManager();
        IConfigurationBuilder builder = configuration;
        // appsettings stand-in < env vars — the order WebApplicationBuilder produces.
        builder.AddInMemoryCollection(new Dictionary<string, string?> { [Key] = "from-appsettings" });
        builder.Add(new EnvironmentVariablesConfigurationSource { Prefix = EnvPrefix });
        // AddElarionSettingsConfiguration appends the settings source last (added last → would win).
        settingsSource = new SettingsConfigurationSource();
        builder.Add(settingsSource);
        return configuration;
    }

    private static void ApplyLive(SettingsConfigurationSource source, string key, string value) =>
        source.Provider.Apply([new SettingEntry(key, value, DateTimeOffset.UtcNow, 1)]);

    [Fact]
    public void WithoutTheLayeringFix_TheStoreWouldBeatTheEnvironment() {
        var configuration = BuildLikeTheHost(out var settingsSource);
        ApplyLive(settingsSource, Key, "from-store");
        // This is the inverted precedence the fix exists to correct.
        Assert.Equal("from-store", configuration[Key]);
    }

    [Fact]
    public void EnvironmentWinsOverTheStore_AndTheStoreOverAppsettings() {
        var configuration = BuildLikeTheHost(out var settingsSource);
        RuntimeSettingsLayering.MakeEnvironmentWin(configuration, []);

        // Env-pinned key: the store write must not take effect.
        ApplyLive(settingsSource, Key, "from-store");
        Assert.Equal("from-env", configuration[Key]);

        // A key with no env pin: the store overrides the appsettings default.
        var configuration2 = BuildLikeTheHost(out var settingsSource2);
        RuntimeSettingsLayering.MakeEnvironmentWin(configuration2, []);
        ApplyLive(settingsSource2, "Watchtower:UnpinnedProbe", "from-store");
        Assert.Equal("from-store", configuration2["Watchtower:UnpinnedProbe"]);
    }

    [Fact]
    public void BootSnapshotIsVisibleImmediately_UntilTheLiveProviderTakesOver() {
        var configuration = BuildLikeTheHost(out var settingsSource);
        RuntimeSettingsLayering.MakeEnvironmentWin(configuration, [
            new KeyValuePair<string, string?>("Watchtower:Auth:Enabled", "true"),
        ]);

        // Pre-DI reads (Program.cs) see the stored value synchronously — no hosted service has run.
        Assert.Equal("true", configuration["Watchtower:Auth:Enabled"]);

        // Once the refresher pushes live data, it shadows the snapshot (runtime edits keep working).
        ApplyLive(settingsSource, "Watchtower:Auth:Enabled", "false");
        Assert.Equal("false", configuration["Watchtower:Auth:Enabled"]);
    }

    [Fact]
    public void BootSnapshotNeverBeatsTheEnvironment() {
        var configuration = BuildLikeTheHost(out _);
        RuntimeSettingsLayering.MakeEnvironmentWin(configuration, [
            new KeyValuePair<string, string?>(Key, "from-boot-snapshot"),
        ]);
        Assert.Equal("from-env", configuration[Key]);
    }

    // ── The boot snapshot reader ─────────────────────────────────────────────

    [Fact]
    public void LoadStoredGlobalSettings_ReadsOnlyTheGlobalScope() {
        // A migrated database, because the reader queries the real elarion_settings table — a
        // hand-written stand-in would keep passing after Elarion changed the table under it.
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            using (var connection = new NpgsqlConnection(connectionString)) {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO elarion_settings (kind, owner, "key", value, updated_on_utc, version)
                    VALUES ('global', '', 'Watchtower:Auth:Enabled', 'true', TIMESTAMPTZ '2026-01-01 00:00:00+00', 1),
                           ('user', '42', 'Watchtower:Auth:Enabled', 'false', TIMESTAMPTZ '2026-01-01 00:00:00+00', 1);
                    """;
                command.ExecuteNonQuery();
            }

            var entries = RuntimeSettingsLayering.LoadStoredGlobalSettings(connectionString);

            var entry = Assert.Single(entries);
            Assert.Equal("Watchtower:Auth:Enabled", entry.Key);
            Assert.Equal("true", entry.Value);
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>
    /// First boot: the database is there but the migration has not run, so <c>elarion_settings</c> does
    /// not exist yet. The snapshot has to come back empty rather than take the host down with it.
    /// </summary>
    [Fact]
    public void LoadStoredGlobalSettings_ToleratesAnUnmigratedDatabase() {
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            using (var connection = new NpgsqlConnection(connectionString)) {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """DROP TABLE "elarion_settings" """;
                command.ExecuteNonQuery();
            }
            Assert.Empty(RuntimeSettingsLayering.LoadStoredGlobalSettings(connectionString));
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>And a database that is not up yet, which is the same startup race one step earlier.</summary>
    [Fact]
    public void LoadStoredGlobalSettings_ToleratesAnUnreachableServer() =>
        Assert.Empty(RuntimeSettingsLayering.LoadStoredGlobalSettings(
            "Host=127.0.0.1;Port=1;Database=watchtower;Username=watchtower;Password=watchtower;Timeout=1"));

    /// <summary>No connection string at all is not an error here either — the host reports that itself.</summary>
    [Fact]
    public void LoadStoredGlobalSettings_ToleratesNoConnectionString() =>
        Assert.Empty(RuntimeSettingsLayering.LoadStoredGlobalSettings(null));
}
