using System.Collections.Immutable;

namespace Watchtower.Application.Services;

/// <summary>The database engines a backup run knows how to dump logically instead of snapshotting.</summary>
public enum DumpEngine {
    /// <summary>PostgreSQL, dumped with <c>pg_dumpall</c> and replayed with <c>psql</c>.</summary>
    Postgres,
}

/// <summary>One database container the run dumps instead of snapshotting its data directory.</summary>
/// <param name="ContainerId">Engine id, for the exec calls the dump is made of.</param>
/// <param name="ContainerName">Operator-facing container name, recorded in the manifest.</param>
/// <param name="Service">The compose service — the dump's identity in the archive and on restore.</param>
/// <param name="Image">The image reference the detection ran against, for the log line and the manifest.</param>
/// <param name="Engine">Which engine's tooling to use.</param>
/// <param name="DataVolume">
/// The named volume mounted at (or above) the engine's data directory, which the dump replaces in the
/// archive. Null when the data directory is a bind mount, an anonymous volume or the container's own
/// writable layer — the dump still happens, nothing is excluded from the file snapshot.
/// </param>
/// <param name="MountedVolumes">Every named volume the container mounts, in the engine's order.</param>
public sealed record DumpTarget(
    string ContainerId,
    string ContainerName,
    string Service,
    string Image,
    DumpEngine Engine,
    string? DataVolume,
    IReadOnlyList<string> MountedVolumes);

/// <summary>
/// Decides which of a compose project's containers are dumped logically rather than snapshotted
/// (ADR-0017). Pure and synchronous: everything it needs about a container is in the engine's listing
/// plus the one environment value <see cref="Select"/> is handed, so the whole policy — detection,
/// label precedence, the demotions — is testable without a daemon.
/// </summary>
/// <remarks>
/// Detection is an <em>exact repository-name match</em>, never a substring: the ecosystem is full of
/// images whose names contain "postgres" while being something else entirely (<c>postgrest</c>,
/// <c>postgres-exporter</c>, <c>postgresql-repmgr</c>), and running <c>pg_dumpall</c> against one of
/// those would fail the backup of a stack that has no database at all. The escape hatch for an image
/// this list does not know is the explicit <c>watchtower.backup.dump=postgres</c> label.
/// </remarks>
public static class DatabaseDumpTargets {
    /// <summary>
    /// Repository names (last path segment, lowercased) that run a PostgreSQL server. The official
    /// image plus the extension distributions that keep its entrypoint, environment variables and
    /// client tooling — which is what the dump actually depends on.
    /// </summary>
    internal static readonly ImmutableHashSet<string> PostgresRepositories = [
        "postgres", "postgresql", "postgis", "pgvector", "timescaledb", "timescaledb-ha", "pgautoupgrade",
    ];

    /// <summary>Where the official image keeps its data directory when <c>PGDATA</c> is unset.</summary>
    internal const string DefaultDataDirectory = "/var/lib/postgresql/data";

    /// <summary>
    /// Bitnami's data root. Its images set <c>PGDATA</c> below this path, but a container started from
    /// an older tag may not report it, so the path is accepted on its own as well.
    /// </summary>
    internal const string BitnamiDataDirectory = "/bitnami/postgresql";

    /// <summary>Label values meaning "do not dump this service, snapshot its volumes as before".</summary>
    private static readonly ImmutableHashSet<string> OptOutValues = ["false", "0", "off", "no"];

    /// <summary>Label values meaning "dump this service with whatever engine its image says".</summary>
    private static readonly ImmutableHashSet<string> OptInValues = ["true", "1", "on", "yes"];

    /// <summary>Label values naming the Postgres engine explicitly, image detection bypassed.</summary>
    private static readonly ImmutableHashSet<string> PostgresValues = ["postgres", "postgresql"];

    /// <summary>
    /// The containers <see cref="Select"/> could turn into dump targets, so the caller knows the (small)
    /// set worth an inspect call for its <c>PGDATA</c> — inspecting every container of a project to find
    /// the one database would be a round-trip per service on every single run.
    /// </summary>
    /// <param name="projectContainers">Every container of the compose project, any state.</param>
    /// <returns>The running containers the label/image rule selects, in the engine's order.</returns>
    public static IReadOnlyList<DockerContainerInfo> Candidates(
        IReadOnlyList<DockerContainerInfo> projectContainers) => [
        .. projectContainers.Where(c =>
            string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)
            && Classify(BackupContainer.FromDocker(c), c.Image).Engine is not null),
    ];

    /// <summary>
    /// Applies the label and image rules to a project's containers and reports what it decided.
    /// </summary>
    /// <param name="projectContainers">Every container of the compose project, any state.</param>
    /// <param name="pgDataByContainerId">
    /// Container id → the container's <c>PGDATA</c> environment value, for the candidates the caller
    /// inspected (see <see cref="Candidates"/>). A missing or null entry means "not set", which is the
    /// official image's default and resolves to <see cref="DefaultDataDirectory"/>.
    /// </param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <returns>The dump targets, ordered by service name.</returns>
    public static IReadOnlyList<DumpTarget> Select(
        IReadOnlyList<DockerContainerInfo> projectContainers,
        IReadOnlyDictionary<string, string?> pgDataByContainerId,
        Action<string> log) {
        var targets = new List<DumpTarget>();
        foreach (var info in projectContainers) {
            var container = BackupContainer.FromDocker(info);
            var name = Describe(container);
            var (engine, warning) = Classify(container, info.Image);
            if (warning is not null) log(warning);
            if (engine is not { } dumpEngine) continue;

            if (!container.IsRunning) {
                // A dump needs a live server. The volumes stay in the archive instead, which is the
                // pre-dump behaviour and — with the database down — a consistent snapshot anyway.
                log($"WARNING: {name} would be dumped but its container is "
                    + $"{(string.IsNullOrWhiteSpace(info.State) ? "not running" : info.State)} "
                    + "— snapshotting its volume(s) instead.");
                continue;
            }

            var dataDirectory = pgDataByContainerId.TryGetValue(info.Id, out var pgData) ? pgData : null;
            var dataVolume = FindDataVolume(info, dataDirectory);
            targets.Add(new DumpTarget(
                info.Id, container.DisplayName, container.Service ?? container.DisplayName,
                info.Image, dumpEngine, dataVolume, container.VolumeNames));

            log($"{name} is postgres ({info.Image}) — dumping with pg_dumpall; "
                + (dataVolume is null
                    ? "its data directory is not a named volume, so nothing is excluded from the file snapshot."
                    : $"volume {dataVolume} is excluded from the file snapshot."));
            var others = container.VolumeNames
                .Where(v => !string.Equals(v, dataVolume, StringComparison.Ordinal))
                .ToList();
            if (others.Count > 0)
                log($"{name} also mounts {string.Join(", ", others)} — not its data directory, "
                    + "so those are still snapshotted.");
        }

        targets.Sort((a, b) => string.CompareOrdinal(a.Service, b.Service));
        return targets;
    }

    /// <summary>
    /// The label/image rule, first match wins: an excluded service is never a target; an explicit
    /// opt-out wins over any detection; an explicitly named engine wins over the image; otherwise the
    /// image decides. A label value that is none of the above is reported and the image decides — the
    /// operator meant <em>something</em>, and silently doing nothing would look identical to a service
    /// that was never labelled.
    /// </summary>
    /// <returns>The engine to dump with (null for "not a dump target") and a line to log, if any.</returns>
    private static (DumpEngine? Engine, string? Warning) Classify(BackupContainer container, string image) {
        var name = Describe(container);
        if (bool.TryParse(container.ExcludeLabel, out var excluded) && excluded) return (null, null);

        var label = container.DumpLabel?.Trim().ToLowerInvariant();
        if (label is { Length: > 0 }) {
            if (OptOutValues.Contains(label))
                return (null, $"{name} opted out of dumps ({BackupPlan.DumpLabel}={container.DumpLabel!.Trim()}) "
                    + "— snapshotting its volume(s) instead.");
            if (PostgresValues.Contains(label)) return (DumpEngine.Postgres, null);
            if (OptInValues.Contains(label))
                return IsPostgresImage(image)
                    ? (DumpEngine.Postgres, null)
                    : (null, $"WARNING: {name} is labelled {BackupPlan.DumpLabel}={container.DumpLabel!.Trim()} but "
                        + $"'{image}' is not a recognized database image — name the engine "
                        + $"({BackupPlan.DumpLabel}=postgres) to dump it anyway; snapshotting its volume(s) instead.");
            return IsPostgresImage(image)
                ? (DumpEngine.Postgres,
                    $"WARNING: {name} has an unrecognized {BackupPlan.DumpLabel} value "
                    + $"'{container.DumpLabel!.Trim()}' — expected \"postgres\", \"true\" or \"false\"; "
                    + "going by its image instead.")
                : (null,
                    $"WARNING: {name} has an unrecognized {BackupPlan.DumpLabel} value "
                    + $"'{container.DumpLabel!.Trim()}' — expected \"postgres\", \"true\" or \"false\"; ignoring it.");
        }

        return (IsPostgresImage(image) ? DumpEngine.Postgres : null, null);
    }

    /// <summary>
    /// The named volume whose mount point is the engine's data directory, or a directory above it —
    /// <c>PGDATA=/var/lib/postgresql/data/pgdata</c> with the volume mounted one level up at
    /// <c>/var/lib/postgresql/data</c> is the shape the official image's own documentation recommends.
    /// </summary>
    private static string? FindDataVolume(DockerContainerInfo container, string? pgData) {
        string[] dataDirectories = [
            string.IsNullOrWhiteSpace(pgData) ? DefaultDataDirectory : pgData.Trim(),
            BitnamiDataDirectory,
        ];
        foreach (var directory in dataDirectories)
            foreach (var mount in container.Mounts ?? []) {
                if (!string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(mount.Name)) continue; // anonymous — nothing to exclude by name
                if (Covers(mount.Destination, directory)) return mount.Name;
            }
        return null;
    }

    /// <summary>Whether <paramref name="mountPoint"/> is <paramref name="path"/> or a directory above it.</summary>
    private static bool Covers(string mountPoint, string path) {
        var mount = mountPoint.TrimEnd('/');
        var target = path.TrimEnd('/');
        if (mount.Length == 0) return false;
        return string.Equals(mount, target, StringComparison.Ordinal)
            || target.StartsWith(mount + "/", StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="imageReference"/> names an image that runs a PostgreSQL server.</summary>
    internal static bool IsPostgresImage(string imageReference) =>
        PostgresRepositories.Contains(RepositoryName(imageReference));

    /// <summary>
    /// The repository name of an image reference, lowercased: registry, path and tag or digest removed.
    /// A tag is only what follows the <em>last</em> colon when that colon comes after the last slash —
    /// otherwise the colon belongs to a registry's port (<c>registry:5000/postgres</c>), and taking it
    /// for a tag would leave the wrong name behind.
    /// </summary>
    internal static string RepositoryName(string imageReference) {
        var reference = imageReference.Trim();
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0) reference = reference[..at];

        var lastColon = reference.LastIndexOf(':');
        var lastSlash = reference.LastIndexOf('/');
        if (lastColon > lastSlash) reference = reference[..lastColon];

        var slash = reference.LastIndexOf('/');
        if (slash >= 0) reference = reference[(slash + 1)..];
        return reference.ToLowerInvariant();
    }

    /// <summary>How a container is named in a log line: its service, else the container itself.</summary>
    private static string Describe(BackupContainer container) =>
        container.Service is { } service ? $"Service '{service}'" : $"Container '{container.DisplayName}'";
}
