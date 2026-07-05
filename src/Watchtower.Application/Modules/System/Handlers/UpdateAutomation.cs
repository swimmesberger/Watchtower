using Elarion.Settings;
using Watchtower.Application.Config;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Persists the automation toggles as Global-scope settings under the <c>Watchtower:*</c> keys, so
/// they layer over the env/appsettings defaults via the settings-backed configuration provider and
/// re-bind into <see cref="WatchtowerOptions"/> at runtime (no restart). The background checkers and
/// <c>system.getAutomation</c> then observe the new effective values through <c>IOptionsMonitor</c>.
/// </summary>
[Handler("system.updateAutomation")]
public sealed class UpdateAutomation(ISettingsManager settings)
    : IHandler<UpdateAutomation.Command, Result<UpdateAutomation.Response>> {
    public sealed record Command(
        bool AutoCheckEnabled,
        int AutoCheckIntervalMinutes,
        bool StackCheckEnabled,
        int StackCheckIntervalMinutes);

    public sealed record Response(
        bool AutoCheckEnabled,
        int AutoCheckIntervalMinutes,
        bool StackCheckEnabled,
        int StackCheckIntervalMinutes);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        await settings.SetStringAsync("Watchtower:AutoCheckEnabled",
            command.AutoCheckEnabled ? "true" : "false", SettingsScope.Global, expectedVersion: null, ct);
        await settings.SetStringAsync("Watchtower:AutoCheckIntervalMinutes",
            command.AutoCheckIntervalMinutes.ToString(), SettingsScope.Global, expectedVersion: null, ct);
        await settings.SetStringAsync("Watchtower:StackCheckEnabled",
            command.StackCheckEnabled ? "true" : "false", SettingsScope.Global, expectedVersion: null, ct);
        await settings.SetStringAsync("Watchtower:StackCheckIntervalMinutes",
            command.StackCheckIntervalMinutes.ToString(), SettingsScope.Global, expectedVersion: null, ct);

        // Echo back exactly what was persisted. The config provider reloads asynchronously, so
        // IOptionsMonitor.CurrentValue may lag by a moment; returning the written values gives the
        // caller an immediately-consistent view.
        return new Response(
            command.AutoCheckEnabled,
            command.AutoCheckIntervalMinutes,
            command.StackCheckEnabled,
            command.StackCheckIntervalMinutes);
    }
}
