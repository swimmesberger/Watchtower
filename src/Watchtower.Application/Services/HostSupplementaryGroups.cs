namespace Watchtower.Application.Services;

/// <summary>
/// The supplementary group ids of the Watchtower process, read from <c>/proc/self/status</c>.
/// <para>
/// Every container Watchtower spawns with <c>/var/run/docker.sock</c> mounted has to be handed these
/// ids as <c>HostConfig.GroupAdd</c>. The socket is owned by the host's <c>docker</c> group, whose
/// numeric id the operator grants Watchtower itself (<c>group_add: ["999"]</c> in compose — see
/// deploy/docker/docker-compose.yml). Inside a spawned container that id resolves to nothing, so a
/// non-root process there gets "permission denied" on the socket unless the id is added explicitly.
/// Consumers: the self-update coordinator and the CI runner containers of docker-socket repos.
/// </para>
/// </summary>
internal static class HostSupplementaryGroups {

    /// <summary>
    /// The current process's supplementary group ids, or an empty array on non-Linux hosts and
    /// anywhere procfs is unavailable (callers then simply pass no <c>GroupAdd</c>).
    /// </summary>
    internal static string[] Current() {
        try {
            foreach (var line in File.ReadLines("/proc/self/status")) {
                if (!line.StartsWith("Groups:", StringComparison.Ordinal)) continue;
                return ParseGroupsLine(line);
            }
        } catch {
            // Non-Linux or procfs unavailable — fall through and return empty.
        }
        return [];
    }

    /// <summary>
    /// The <c>Groups:</c> line separates the label from the ids with a tab and the ids with spaces
    /// (e.g. <c>"Groups:\t0 100 "</c>). Both must be treated as separators: an id that keeps a
    /// leading tab is no longer numeric to Docker, which then tries it as a group <em>name</em>
    /// against the image's <c>/etc/group</c> and fails the container start with
    /// "unable to find group 0: no matching entries in group file".
    /// </summary>
    internal static string[] ParseGroupsLine(string line) =>
        line["Groups:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
}
