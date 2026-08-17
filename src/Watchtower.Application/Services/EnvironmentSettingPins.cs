using System.Collections;

namespace Watchtower.Application.Services;

/// <summary>
/// Reports which Watchtower configuration paths are pinned by environment variables. The settings
/// store layers <em>below</em> the environment provider (env wins — see the configuration layering in
/// <c>Program.cs</c>), so a pinned path cannot be changed at runtime: a settings row would persist but
/// never take effect. The settings handlers therefore surface pinned paths (the UI disables those
/// fields with a badge naming the variable) and reject writes that try to change one.
/// </summary>
/// <remarks>
/// The pin set is snapshotted at construction — a process's environment does not change over its
/// lifetime. Only the <c>__</c>-separated convention is considered (<c>WATCHTOWER__AUTH__ENABLED</c> ⇔
/// <c>Watchtower:Auth:Enabled</c>), matching how .NET's environment configuration provider maps
/// variables onto the configuration hierarchy.
/// </remarks>
public sealed class EnvironmentSettingPins {
    private readonly HashSet<string> _pinned;

    /// <summary>Builds the pin set from the process environment.</summary>
    public EnvironmentSettingPins() : this(EnumerateProcessVariableNames()) { }

    /// <summary>Builds the pin set from explicit variable names — the test seam.</summary>
    public EnvironmentSettingPins(IEnumerable<string> environmentVariableNames) {
        _pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in environmentVariableNames) {
            // Only the Watchtower namespace matters; this also keeps the set tiny.
            if (!name.StartsWith("WATCHTOWER", StringComparison.OrdinalIgnoreCase)) continue;
            _pinned.Add(name.Replace("__", ":"));
        }
    }

    /// <summary>True when the configuration path (e.g. <c>Watchtower:Auth:Enabled</c>) is set via its env var.</summary>
    public bool IsPinned(string path) => _pinned.Contains(path);

    /// <summary>The subset of <paramref name="paths"/> that is pinned, for the get-handlers' pinned list.</summary>
    public string[] Pinned(params string[] paths) => paths.Where(IsPinned).ToArray();

    /// <summary>The conventional environment variable name for a configuration path.</summary>
    public static string ToEnvironmentVariableName(string path) =>
        path.Replace(":", "__").ToUpperInvariant();

    /// <summary>The validation error for an attempt to change pinned paths at runtime.</summary>
    public static AppError PinnedError(IReadOnlyCollection<string> changedPinnedPaths) {
        var variables = string.Join(", ", changedPinnedPaths.Select(ToEnvironmentVariableName));
        return AppError.Validation(
            $"Set via environment variable and not editable at runtime: {variables}. " +
            "Remove the variable(s) from the deployment (and restart) to edit these settings here.");
    }

    private static IEnumerable<string> EnumerateProcessVariableNames() {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string name) yield return name;
    }
}
