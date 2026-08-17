using Elarion.Settings.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace Watchtower.Api;

/// <summary>
/// Arranges the configuration layers so that <b>environment variables win over runtime settings</b>.
/// <para>
/// <c>AddElarionSettingsConfiguration</c> appends the settings-store source, which would make DB-stored
/// values beat <c>WATCHTOWER__*</c> env vars. That inverts the operator contract this deployment wants:
/// env vars are infrastructure-as-code pins — what the compose file says is what runs — and the
/// Settings UI disables env-pinned fields (<see cref="Application.Services.EnvironmentSettingPins"/>)
/// instead of silently losing to them. It also keeps <c>WATCHTOWER__AUTH__ENABLED=false</c> + restart
/// working as the lockout escape hatch. <see cref="MakeEnvironmentWin"/> therefore moves the settings
/// source (plus a boot-time snapshot of the stored values) below the first environment-variable source:
/// <c>appsettings &lt; boot snapshot &lt; live settings store &lt; env vars &lt; command line</c>.
/// </para>
/// <para>
/// The boot snapshot exists because the live provider's data is only pushed by a hosted service
/// (<c>SettingsConfigurationRefresher.StartAsync</c>) — after the pre-DI configuration reads in
/// <c>Program.cs</c> (<c>Auth:Enabled</c>, <c>Auth:KeyPath</c>) have already happened. Without it, a
/// stored <c>Auth:Enabled</c> would never reach the startup pipeline decision and the setting could not
/// survive a restart. <see cref="LoadStoredGlobalSettings"/> reads the <c>elarion_settings</c> table
/// synchronously (read-only, tolerant of a missing file/table on first run) so those reads see the
/// stored values; the live source sits directly above it and takes over once the refresher runs.
/// </para>
/// </summary>
public static class RuntimeSettingsLayering {
    /// <summary>
    /// Repositions the (already added) Elarion settings source below the environment-variable
    /// provider and inserts <paramref name="storedSettings"/> as a boot-time snapshot directly
    /// beneath it. No-ops on the snapshot when <paramref name="storedSettings"/> is empty.
    /// </summary>
    public static void MakeEnvironmentWin(
        IConfigurationBuilder configuration,
        IEnumerable<KeyValuePair<string, string?>> storedSettings) {
        var sources = configuration.Sources;

        var settingsIndex = -1;
        for (var i = sources.Count - 1; i >= 0; i--) {
            if (sources[i] is not SettingsConfigurationSource) continue;
            settingsIndex = i;
            break;
        }
        if (settingsIndex < 0)
            throw new InvalidOperationException(
                "AddElarionSettingsConfiguration must be called before MakeEnvironmentWin.");

        var settingsSource = sources[settingsIndex];
        sources.RemoveAt(settingsIndex);

        var envIndex = -1;
        for (var i = 0; i < sources.Count; i++) {
            if (sources[i] is not EnvironmentVariablesConfigurationSource) continue;
            envIndex = i;
            break;
        }
        // No env source (test hosts): append at the end — precedence relative to env is then moot.
        var insertAt = envIndex < 0 ? sources.Count : envIndex;

        var snapshot = storedSettings.ToList();
        if (snapshot.Count > 0) {
            sources.Insert(insertAt, new MemoryConfigurationSource { InitialData = snapshot });
            insertAt++;
        }
        sources.Insert(insertAt, settingsSource);
    }

    /// <summary>
    /// Synchronously reads every Global-scope setting from the SQLite database at
    /// <paramref name="dbPath"/>. Returns an empty list when the file or table does not exist yet
    /// (first run, or an upgrade whose migration has not run) — the live provider fills the gap once
    /// the host has started.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> LoadStoredGlobalSettings(string dbPath) {
        try {
            if (!File.Exists(dbPath)) return [];
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            // Scope encoding per Elarion.Settings: Global is kind "global" with an empty owner.
            command.CommandText = """SELECT "key", "value" FROM elarion_settings WHERE kind = 'global' AND owner = ''""";
            using var reader = command.ExecuteReader();
            var entries = new List<KeyValuePair<string, string?>>();
            while (reader.Read())
                entries.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            return entries;
        } catch (Exception ex) {
            // Never fail startup over the snapshot: pre-DI reads fall back to env/appsettings and the
            // live provider still loads once the host runs. Logging isn't built yet, hence Console.
            Console.Error.WriteLine($"warn: could not preload stored settings from {dbPath}: {ex.Message}");
            return [];
        }
    }
}
