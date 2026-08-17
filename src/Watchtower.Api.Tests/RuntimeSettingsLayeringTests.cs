using Elarion.Settings;
using Elarion.Settings.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
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
        var dbPath = Path.Combine(Path.GetTempPath(), "watchtower-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try {
            using (var connection = new SqliteConnection($"Data Source={dbPath}")) {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE elarion_settings (
                        kind TEXT NOT NULL, owner TEXT NOT NULL, "key" TEXT NOT NULL,
                        value TEXT, updated_on_utc TEXT NOT NULL, version INTEGER NOT NULL,
                        PRIMARY KEY (kind, owner, "key"));
                    INSERT INTO elarion_settings VALUES ('global', '', 'Watchtower:Auth:Enabled', 'true', '2026-01-01', 1);
                    INSERT INTO elarion_settings VALUES ('user', '42', 'Watchtower:Auth:Enabled', 'false', '2026-01-01', 1);
                    """;
                command.ExecuteNonQuery();
            }

            var entries = RuntimeSettingsLayering.LoadStoredGlobalSettings(dbPath);

            var entry = Assert.Single(entries);
            Assert.Equal("Watchtower:Auth:Enabled", entry.Key);
            Assert.Equal("true", entry.Value);
        } finally {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void LoadStoredGlobalSettings_ToleratesAMissingDatabase() =>
        Assert.Empty(RuntimeSettingsLayering.LoadStoredGlobalSettings(
            Path.Combine(Path.GetTempPath(), "watchtower-tests", $"{Guid.NewGuid():N}-missing.db")));
}
