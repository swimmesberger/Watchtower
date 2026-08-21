using Elarion.Abstractions.Identity;
using Elarion.Settings;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Persists the automation toggles as Global-scope settings under the <c>Watchtower:*</c> keys, so
/// they layer over the appsettings defaults via the settings-backed configuration provider and
/// re-bind into <see cref="WatchtowerOptions"/> at runtime (no restart). The background checkers and
/// <c>system.getAutomation</c> then observe the new effective values through <c>IOptionsMonitor</c>.
/// A toggle pinned by its <c>WATCHTOWER__*</c> env var (which wins over the store) is rejected when the
/// request tries to change it, and never written — a stored row that can't take effect is a lie.
/// </summary>
[Handler("system.updateAutomation")]
public sealed class UpdateAutomation(
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    EnvironmentSettingPins pins,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<UpdateAutomation.Command, Result<UpdateAutomation.Response>> {
    public sealed record Command(
        bool AutoCheckEnabled,
        int AutoCheckIntervalMinutes,
        bool StackCheckEnabled,
        int StackCheckIntervalMinutes,
        bool ImagePruneEnabled,
        int ImagePruneIntervalMinutes);

    public sealed record Response(
        bool AutoCheckEnabled,
        int AutoCheckIntervalMinutes,
        bool StackCheckEnabled,
        int StackCheckIntervalMinutes,
        bool ImagePruneEnabled,
        int ImagePruneIntervalMinutes,
        string[] PinnedPaths);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var effective = options.CurrentValue;
        var writes = new List<(string Path, string Value, bool Changed)> {
            (WatchtowerSettingPaths.AutoCheckEnabled,
                Bool(command.AutoCheckEnabled), command.AutoCheckEnabled != effective.AutoCheckEnabled),
            (WatchtowerSettingPaths.AutoCheckIntervalMinutes,
                command.AutoCheckIntervalMinutes.ToString(), command.AutoCheckIntervalMinutes != effective.AutoCheckIntervalMinutes),
            (WatchtowerSettingPaths.StackCheckEnabled,
                Bool(command.StackCheckEnabled), command.StackCheckEnabled != effective.StackCheckEnabled),
            (WatchtowerSettingPaths.StackCheckIntervalMinutes,
                command.StackCheckIntervalMinutes.ToString(), command.StackCheckIntervalMinutes != effective.StackCheckIntervalMinutes),
            (WatchtowerSettingPaths.ImagePruneEnabled,
                Bool(command.ImagePruneEnabled), command.ImagePruneEnabled != effective.ImagePruneEnabled),
            (WatchtowerSettingPaths.ImagePruneIntervalMinutes,
                command.ImagePruneIntervalMinutes.ToString(), command.ImagePruneIntervalMinutes != effective.ImagePruneIntervalMinutes),
        };

        var violations = writes.Where(w => w.Changed && pins.IsPinned(w.Path)).Select(w => w.Path).ToList();
        if (violations.Count > 0)
            return EnvironmentSettingPins.PinnedError(violations);

        foreach (var (path, value, _) in writes.Where(w => !pins.IsPinned(w.Path)))
            await settings.SetStringAsync(path, value, SettingsScope.Global, expectedVersion: null, ct);

        // Recorded post-write with the new effective toggles.
        await audit.RecordAsync("system", "automation.update", "automation settings",
            string.Join(" · ",
                Toggle("self-update check", command.AutoCheckEnabled, command.AutoCheckIntervalMinutes),
                Toggle("stack check", command.StackCheckEnabled, command.StackCheckIntervalMinutes),
                Toggle("image prune", command.ImagePruneEnabled, command.ImagePruneIntervalMinutes)),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // Echo back exactly what was persisted (pinned values are unchanged by construction). The config
        // provider reloads asynchronously, so IOptionsMonitor.CurrentValue may lag by a moment; returning
        // the written values gives the caller an immediately-consistent view.
        return new Response(
            command.AutoCheckEnabled,
            command.AutoCheckIntervalMinutes,
            command.StackCheckEnabled,
            command.StackCheckIntervalMinutes,
            command.ImagePruneEnabled,
            command.ImagePruneIntervalMinutes,
            pins.Pinned(AutomationPaths));

        static string Bool(bool value) => value ? "true" : "false";
    }

    private static string Toggle(string name, bool on, int minutes) =>
        on ? $"{name} on ({minutes}m)" : $"{name} off";

    /// <summary>Every path this handler manages, in UI order — shared with <see cref="GetAutomation"/>.</summary>
    internal static readonly string[] AutomationPaths = [
        WatchtowerSettingPaths.AutoCheckEnabled,
        WatchtowerSettingPaths.AutoCheckIntervalMinutes,
        WatchtowerSettingPaths.StackCheckEnabled,
        WatchtowerSettingPaths.StackCheckIntervalMinutes,
        WatchtowerSettingPaths.ImagePruneEnabled,
        WatchtowerSettingPaths.ImagePruneIntervalMinutes,
    ];
}
