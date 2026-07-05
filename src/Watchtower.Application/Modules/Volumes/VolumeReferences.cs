using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Volumes;

/// <summary>
/// Shared server-side logic for resolving volume → container references and the three-state
/// lifecycle. Kept in one place so <c>volumes.list</c>, <c>volumes.remove</c> and
/// <c>volumes.pruneOrphans</c> agree on what "orphaned" and "refCount == 0" mean.
/// </summary>
internal static class VolumeReferences {
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeVolumeLabel = "com.docker.compose.volume";

    /// <summary>Lifecycle values (matches the frontend chip contract).</summary>
    public const string Live = "live";
    public const string Declared = "declared";
    public const string Orphaned = "orphaned";

    /// <summary>
    /// Builds a map of volume name → the (deduplicated) names of containers that mount it.
    /// Considers containers in ANY state (running or stopped): a stopped container still holds
    /// a reference that prevents removal. Only named-volume mounts (<c>Type == "volume"</c> with a
    /// non-empty <c>Name</c>) are counted.
    /// </summary>
    public static Dictionary<string, List<string>> BuildInUseMap(IReadOnlyList<DockerContainerInfo> containers) {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in containers) {
            var containerName = PrimaryName(c.Names);
            foreach (var mount in c.Mounts) {
                if (!string.Equals(mount.Type, "volume", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(mount.Name)) continue;
                if (!map.TryGetValue(mount.Name, out var list)) {
                    list = [];
                    map[mount.Name] = list;
                }
                if (!list.Contains(containerName, StringComparer.Ordinal))
                    list.Add(containerName);
            }
        }
        return map;
    }

    /// <summary>
    /// Computes the three-state lifecycle: <c>live</c> when any container references it,
    /// else <c>declared</c> when it carries a compose project label, else <c>orphaned</c>.
    /// </summary>
    public static string ResolveLifecycle(string? project, int refCount) {
        if (refCount > 0) return Live;
        return project is not null ? Declared : Orphaned;
    }

    /// <summary>Strips the Docker leading-slash from a container name (Names are like "/web").</summary>
    public static string PrimaryName(string[] names) {
        if (names.Length == 0) return "";
        var n = names[0];
        return n.StartsWith('/') ? n[1..] : n;
    }
}
