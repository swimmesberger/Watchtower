using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The one-time upgrade step that keeps ADR-0017's default flip from switching a running installation's
/// proxy underneath it. Before ADR-0017 the implicit <c>Proxy:Provider</c> default was <c>caddy</c>, so
/// an operator who added routes never had to name a provider — and after the flip that same silence
/// would mean "the in-process proxy", quietly abandoning a working Caddy container, its certificates and
/// its published ports on nothing more than an image update.
/// </summary>
/// <remarks>
/// <para>
/// <b>It runs exactly once, and the sentinel is why.</b> "Did this instance rely on the old default?" is
/// answerable only on the first start after the upgrade: a route table is not evidence of anything a
/// week later, once a fresh install has added routes of its own under the new default. Keying off the
/// provider row alone would be a trap — nothing writes that row in the normal course of events, so a
/// fresh install that enabled the proxy and created its first routes would satisfy every condition on
/// its *next* restart and be dragged onto Caddy. So <see cref="WatchtowerSettingPaths.ProxyProviderMigrated"/>
/// is written on every path, including the ones that decline to pin, and its presence ends the matter.
/// </para>
/// <para>
/// The pin itself is deliberately narrow: no provider stated anywhere (neither env nor settings store),
/// and at least one route in the table. Note what is <em>not</em> a condition — whether the proxy is
/// currently enabled. A pre-flip instance with routes was a Caddy installation whatever position that
/// toggle happens to be in at this moment, and an operator who switched the proxy off to investigate
/// something should not find it has changed provider when they switch it back on.
/// </para>
/// <para>
/// Run from <c>Program.InitializeDatabaseAsync</c> — after <c>Database.MigrateAsync</c>, because it reads
/// the routes table, and before <c>app.RunAsync()</c>, because the providers decide what to start in
/// their <c>StartAsync</c>. The settings row reaches <c>IOptionsMonitor&lt;WatchtowerOptions&gt;</c> in
/// between: the settings configuration refresher is a hosted service registered ahead of the proxy
/// providers, so it pushes the stored values into configuration before any of them starts.
/// </para>
/// </remarks>
public sealed class ProxyProviderMigration(
    WatchtowerDbContext db,
    ISettingsManager settings,
    EnvironmentSettingPins pins,
    AuditLog audit,
    ILogger<ProxyProviderMigration> logger) {
    /// <summary>The log line an operator sees once, on the first start after the upgrade.</summary>
    internal const string PinnedMessage =
        "Existing reverse-proxy installation detected: pinned provider to caddy (deprecated). " +
        "Switch to the built-in provider under Settings → Reverse proxy.";

    /// <summary>
    /// Decides once whether this installation predates the default flip, pinning it to <c>caddy</c> if so,
    /// and records that the decision has been made either way. Returns true when it pinned the provider.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct = default) {
        // Already decided, on some earlier start. Nothing below is safe to re-evaluate: the route table
        // has moved on since, and re-reading it now would answer a different question.
        var migrated = await settings.GetStringAsync(
            WatchtowerSettingPaths.ProxyProviderMigrated, SettingsScope.Global, ct);
        if (!string.IsNullOrWhiteSpace(migrated)) return false;

        var pinned = await ShouldPinToCaddyAsync(ct);
        if (pinned) {
            await settings.SetStringAsync(
                WatchtowerSettingPaths.ProxyProvider, ProxyProviderNames.Caddy,
                SettingsScope.Global, expectedVersion: null, ct);
            logger.LogInformation(PinnedMessage);
            // Actor-less on purpose: nobody asked for this, the upgrade did. The row is what makes the
            // change explicable months later, when "why is this instance on the deprecated provider?"
            // comes up.
            await audit.RecordAsync(
                "proxy", "config.migrate", "proxy settings",
                "pinned provider to caddy — existing installation upgraded to the yarp default (ADR-0017)",
                ct: ct);
        }

        // Written on every path, pinned or not. A fresh install's very first start has no routes and
        // declines to pin — and this is what stops that same install being pinned on the restart after it
        // adds some.
        await settings.SetStringAsync(
            WatchtowerSettingPaths.ProxyProviderMigrated, "true",
            SettingsScope.Global, expectedVersion: null, ct);
        return pinned;
    }

    /// <summary>Whether this looks like an installation that was running on the old implicit default.</summary>
    private async Task<bool> ShouldPinToCaddyAsync(CancellationToken ct) {
        // Env wins over the store (ADR-0014), so a variable is a stated provider even though the store is
        // empty — and writing a row underneath it would persist a value that never takes effect.
        if (pins.IsPinned(WatchtowerSettingPaths.ProxyProvider)) return false;

        // A stored value is a stated provider too, whichever one it names — including the operator who
        // deliberately moved an existing install onto the in-process proxy. Read from the settings store
        // rather than IConfiguration: configuration cannot tell "stored as caddy" from "defaulted".
        var stored = await settings.GetStringAsync(
            WatchtowerSettingPaths.ProxyProvider, SettingsScope.Global, ct);
        if (!string.IsNullOrWhiteSpace(stored)) return false;

        // The evidence, and the only evidence there is: routes that were being served by something, and
        // before the flip that something was Caddy.
        return await db.Routes.AnyAsync(ct);
    }
}
