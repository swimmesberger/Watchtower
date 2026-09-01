using Elarion.Settings;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services.PortRoutes;

/// <summary>
/// The one-time upgrade step that carries the three port-route settings out of the <c>Proxy:Yarp:</c>
/// namespace they were named in before the ADR-0033 addendum. The names said port routes were the
/// in-process provider's; they never were, and leaving them there would have been the conflation the
/// addendum removes, spelled out in the one place an operator reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It runs exactly once, and the sentinel is why.</b> The old rows are not deleted — a rollback to a
/// build that still reads them has to find them — so "is there an old value to copy?" stays true
/// forever, and without <see cref="WatchtowerSettingPaths.ProxyPortRoutesMigrated"/> every restart
/// would copy it again over whatever the operator has since typed into the new field.
/// </para>
/// <para>
/// <b>The store only, and never over a value.</b> A key that already has a stored value under its new
/// name is left alone: the new name wins the moment anything writes it, and re-imposing the old one
/// would be the upgrade undoing an edit. And what is copied is the <em>stored</em> row, read through
/// <see cref="ISettingsManager"/> rather than out of <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>,
/// because configuration cannot tell a stored value from an environment variable and copying an
/// environment variable into a row would persist a value the operator never asked to persist.
/// </para>
/// <para>
/// That is also the one thing this cannot fix, and it is warned about instead: an operator who pinned
/// <c>WATCHTOWER__PROXY__YARP__LANNAMES</c> in their compose file has a variable that no longer maps to
/// any setting, and no store copy can see it. The warning is emitted on every start — not once ever —
/// because the condition is a line in a deployment file that stays true until somebody edits it, and a
/// sentinel would silence the message for exactly the operator who has not acted on it yet.
/// </para>
/// <para>
/// Run from <c>Program.InitializeDatabaseAsync</c>, next to <see cref="ProxyProviderMigration"/> and for
/// the same reason: before <c>app.RunAsync()</c>, so the copied rows are pushed into configuration by
/// the settings refresher before <see cref="PortRoutePlane"/> and the certificate services start.
/// </para>
/// </remarks>
public sealed class PortRouteSettingsMigration(
    ISettingsManager settings,
    EnvironmentSettingPins pins,
    AuditLog audit,
    ILogger<PortRouteSettingsMigration> logger) {
    /// <summary>The LAN names' pre-addendum path. Kept only so the copy below can find the row.</summary>
    internal const string LegacyLanNames = "Watchtower:Proxy:Yarp:LanNames";

    /// <summary>The listener set's pre-addendum path.</summary>
    internal const string LegacyPorts = "Watchtower:Proxy:Yarp:PortRoutePorts";

    /// <summary>The published-host-port claims' pre-addendum path.</summary>
    internal const string LegacyManagedHostPorts = "Watchtower:Proxy:Yarp:ManagedHostPorts";

    /// <summary>Old path ⇒ new path, in the order the log and the audit row name them.</summary>
    private static readonly (string Old, string New)[] Renames = [
        (LegacyLanNames, WatchtowerSettingPaths.ProxyPortRoutesLanNames),
        (LegacyPorts, WatchtowerSettingPaths.ProxyPortRoutesPorts),
        (LegacyManagedHostPorts, WatchtowerSettingPaths.ProxyPortRoutesManagedHostPorts),
    ];

    /// <summary>
    /// The boot-time half of the rename: reads a snapshot of the stored Global settings and adds
    /// <c>(new key, value of the old key)</c> for each pair whose new key is not in it. Pure — no
    /// database, no logger, no ordering assumptions — and idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RunAsync"/> is the durable half and it runs too late for one boot. The Kestrel
    /// projection is built before the host exists and the certificate ensure runs in
    /// <c>WatchtowerStateInitializer</c>, and both read the synchronous boot snapshot
    /// (<c>RuntimeSettingsLayering.LoadStoredGlobalSettings</c>) — which the migration has not written to
    /// yet, because it runs later in <c>InitializeDatabaseAsync</c>. Without this, the first start after
    /// the upgrade would read <c>Proxy:PortRoutes:Ports</c> and <c>:LanNames</c> as unset: no
    /// <c>ProxyPort{n}</c> endpoint at the initial bind — which also silently downgrades a bind conflict
    /// from fatal-at-startup to stale-after-reload — and every port route stamped
    /// <c>Error: no LAN names</c> until the plane's reconcile corrected it a moment later.
    /// </para>
    /// <para>
    /// An alias, not a write: it shadows nothing an operator has stated. A new key already in the
    /// snapshot wins (the migration has run, or somebody saved the new field), and the environment
    /// provider still sits above the whole snapshot (ADR-0014), so a pin on the new name wins over both.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<KeyValuePair<string, string?>> WithLegacyAliases(
        IEnumerable<KeyValuePair<string, string?>> storedSettings) {
        ArgumentNullException.ThrowIfNull(storedSettings);
        var snapshot = storedSettings.ToList();
        var byKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Last wins, which is the reading a configuration provider gives the same list.
        foreach (var (key, value) in snapshot) byKey[key] = value;

        foreach (var (old, renamed) in Renames) {
            if (byKey.ContainsKey(renamed)) continue;
            if (!byKey.TryGetValue(old, out var value)) continue;
            snapshot.Add(new KeyValuePair<string, string?>(renamed, value));
        }
        return snapshot;
    }

    /// <summary>
    /// Warns about any old name still pinned in the environment, then — once ever — copies each stored
    /// old value onto its new path. Returns the paths it wrote, empty when there was nothing to do.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default) {
        WarnAboutPinnedLegacyNames();

        // Already decided, on some earlier start. The old rows are still there and re-reading them now
        // would answer a different question — "what did this deployment once have?" rather than "what
        // should it start with?".
        var migrated = await settings.GetStringAsync(
            WatchtowerSettingPaths.ProxyPortRoutesMigrated, SettingsScope.Global, ct);
        if (!string.IsNullOrWhiteSpace(migrated)) return [];

        var copied = new List<string>();
        foreach (var (old, renamed) in Renames) {
            // The new name wins wherever it already says something — including an empty string somebody
            // deliberately saved, which is why this is a null/blank check on the raw row rather than a
            // value comparison.
            var current = await settings.GetStringAsync(renamed, SettingsScope.Global, ct);
            if (current is not null) continue;

            var stored = await settings.GetStringAsync(old, SettingsScope.Global, ct);
            if (stored is null) continue;

            await settings.SetStringAsync(renamed, stored, SettingsScope.Global, expectedVersion: null, ct);
            copied.Add(renamed);
        }

        if (copied.Count > 0) {
            logger.LogInformation(
                "Port-route settings renamed out of the Proxy:Yarp namespace (ADR-0033 addendum); copied "
                + "{Count} stored value(s): {Paths}.", copied.Count, string.Join(", ", copied));
            // Actor-less on purpose: nobody asked for this, the upgrade did. The row is what makes the
            // change explicable months later, when "why does this deployment have two of these rows?"
            // comes up.
            await audit.RecordAsync(
                "proxy", "config.migrate", "proxy settings",
                $"copied the port-route settings to Watchtower:Proxy:PortRoutes:* — {string.Join(", ", copied)} "
                + "(ADR-0033 addendum: port routes are provider-independent)",
                ct: ct);
        }

        // Written on every path, copied or not. A fresh install has no old rows and copies nothing — and
        // this is what stops it copying one the day somebody restores an old backup alongside it.
        await settings.SetStringAsync(
            WatchtowerSettingPaths.ProxyPortRoutesMigrated, "true",
            SettingsScope.Global, expectedVersion: null, ct);
        return copied;
    }

    /// <summary>
    /// Names any environment variable still pinning an old path. It is not ignored quietly: env wins
    /// over the store (ADR-0014), so before the rename that variable <em>was</em> the setting, and after
    /// it the variable maps to nothing at all — the deployment silently loses whatever it stated.
    /// </summary>
    private void WarnAboutPinnedLegacyNames() {
        foreach (var (old, renamed) in Renames) {
            if (!pins.IsPinned(old)) continue;
            logger.LogWarning(
                "{OldVariable} is set but no longer has any effect: the setting is now {NewPath} "
                + "({NewVariable}). Environment values are invisible to the settings store, so nothing "
                + "copied it across — set the new variable (or remove the old one and use Settings → "
                + "Reverse proxy).",
                EnvironmentSettingPins.ToEnvironmentVariableName(old), renamed,
                EnvironmentSettingPins.ToEnvironmentVariableName(renamed));
        }
    }
}
