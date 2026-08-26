using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchtower.Application.Services;

/// <summary>One dump in the archive, matched to the container it is to be replayed into.</summary>
/// <param name="File">Path inside the archive, relative to <c>backup/</c> (<c>_dumps/db.sql</c>).</param>
/// <param name="Service">The compose service the dump came from and goes back into.</param>
/// <param name="Engine">Which engine's tooling replays it.</param>
/// <param name="User">
/// The role the dump was taken as, from the manifest; null when the manifest did not say, in which
/// case the replay uses whatever the target container's own environment declares.
/// </param>
/// <param name="ExpectedDatabases">
/// The databases the manifest says the dump contains. The restore succeeds only if all of them exist
/// afterwards — psql's exit code cannot be that judge, see <see cref="PostgresReplayOutcome"/>.
/// </param>
/// <param name="ContainerId">The container to replay into.</param>
/// <param name="ContainerName">Its name, for the run output.</param>
public sealed record PlannedReplay(
    string File,
    string Service,
    DumpEngine Engine,
    string? User,
    IReadOnlyList<string> ExpectedDatabases,
    string ContainerId,
    string ContainerName);

/// <summary>
/// Matches an archive's dumps against the stack as it exists on this host (ADR-0017 §5), before the
/// restore touches anything. Pure: it reads the table of contents, the manifest and the engine's
/// container listing, and decides — no I/O, so every refusal is testable without a daemon.
/// </summary>
/// <remarks>
/// <para>
/// The table of contents is physical truth and the manifest is metadata: a dump file the manifest
/// forgot is still replayed (its service read off the file name), while a manifest entry whose file
/// is missing refuses the restore — the archive is then not what it says it is, and continuing would
/// wipe the volumes for a database that never gets its data back.
/// </para>
/// <para>
/// Everything that would make a replay land somewhere wrong is an error rather than a skipped step:
/// no container for the service, a container that no longer runs Postgres, an engine this version
/// cannot replay. All of them are collected, so one refusal reports every problem at once.
/// </para>
/// </remarks>
/// <param name="Replays">The dumps to replay, ordered by service.</param>
/// <param name="Errors">
/// Reasons the archive cannot be restored into this stack, un-prefixed; any entry means the caller
/// must refuse before stopping a container or wiping a volume.
/// </param>
/// <param name="Warnings">Operator-facing lines, un-prefixed — the caller marks them up.</param>
/// <param name="DumpCoveredVolumes">
/// Volumes the archive deliberately does not contain because a dump stands in for them. The restore
/// reports these as covered instead of warning that a host volume is missing from the archive.
/// </param>
public sealed record RestoreDumpPlan(
    IReadOnlyList<PlannedReplay> Replays,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DumpCoveredVolumes) {

    /// <summary>
    /// Volume name → the service whose dump covers it, so the run output can name it. Parallel to
    /// <see cref="DumpCoveredVolumes"/>, kept off the constructor because it is a convenience for one
    /// log line rather than part of the decision.
    /// </summary>
    public IReadOnlyDictionary<string, string> CoveredBy { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The archive format this version knows how to read in full.</summary>
    /// <remarks>
    /// <b>Tied to the writer, not spelled again.</b> The two drifted apart exactly once — stage 7 of
    /// ADR-0026 raised the written manifest to 3 and left this at 2, so every restore of this build's
    /// own archives would have printed the "newer than this Watchtower understands" warning at the
    /// operator. A reader that is behind its own writer is never right, and the only way to keep them
    /// in step is for there to be one number. A future format the reader genuinely cannot follow is a
    /// *reader* change (new keys understood here) landing with the writer bump, not a second constant.
    /// </remarks>
    internal const int KnownFormatVersion = BackupService.ManifestFormatVersion;

    /// <summary>Manifest engine values that mean PostgreSQL.</summary>
    private static readonly HashSet<string> PostgresEngines =
        new(["postgres", "postgresql"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Matches the archive's dumps against the project's containers.</summary>
    /// <param name="contents">The archive's table of contents, including its manifest.</param>
    /// <param name="projectContainers">Every container of the compose project, in any state.</param>
    /// <returns>The replays to run, plus everything that stands in their way.</returns>
    public static RestoreDumpPlan Match(
        BackupArchiveContents contents, IReadOnlyList<DockerContainerInfo> projectContainers) {
        var errors = new List<string>();
        var warnings = new List<string>();
        var (entries, formatVersion, unreadable) = ReadManifest(contents.ManifestJson);
        if (unreadable is not null) warnings.Add(unreadable);
        if (formatVersion > KnownFormatVersion)
            warnings.Add(
                $"the archive says formatVersion {formatVersion}, which is newer than this Watchtower "
                + $"understands (it reads up to {KnownFormatVersion}) — anything it added beyond the "
                + "volumes and dumps below is ignored.");

        var covered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            foreach (var volume in entry.Volumes)
                covered.TryAdd(volume, entry.Service);

        // A manifest entry without its file: the archive is incomplete, and a restore that proceeded
        // would wipe the volumes without ever putting the database back.
        foreach (var entry in entries.Where(e => !contents.DumpFiles.Contains(e.File, StringComparer.Ordinal)))
            errors.Add(
                $"the manifest lists a dump of service '{entry.Service}' at 'backup/{entry.File}', but the "
                + "archive does not contain that file.");

        var byService = projectContainers
            .Select(c => (Info: c, Planner: BackupContainer.FromDocker(c)))
            .Where(c => c.Planner.Service is not null)
            .GroupBy(c => c.Planner.Service!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Planner.DisplayName, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        var replays = new List<PlannedReplay>();
        foreach (var file in contents.DumpFiles) {
            var entry = entries.FirstOrDefault(e => string.Equals(e.File, file, StringComparison.Ordinal));
            var service = entry?.Service ?? ServiceFromFileName(file);
            if (entry is null)
                warnings.Add(
                    $"the archive contains 'backup/{file}', which the manifest does not describe — "
                    + $"replaying it into service '{service}', going by the file name.");

            if (ResolveEngine(entry?.Engine, file) is not { } engine) {
                errors.Add(
                    $"the dump 'backup/{file}' declares engine '{entry?.Engine}', which this Watchtower "
                    + "cannot replay — only postgres dumps are supported.");
                continue;
            }

            if (!byService.TryGetValue(service, out var candidates)) {
                errors.Add(
                    $"the archive carries a dump for service '{service}', but this stack has no container "
                    + "for that service — deploy the stack (or restore into the stack the archive came "
                    + "from) first.");
                continue;
            }
            if (candidates.Count > 1)
                warnings.Add(
                    $"service '{service}' has {candidates.Count} containers; the dump is replayed into "
                    + $"{candidates[0].Planner.DisplayName} — the others share the same server anyway.");

            var chosen = candidates[0];
            if (!IsPostgres(chosen.Info, chosen.Planner)) {
                errors.Add(
                    $"service '{service}' now runs '{chosen.Info.Image}', which is not a Postgres image — "
                    + $"refusing to replay a Postgres dump into it (label it {BackupPlan.DumpLabel}=postgres "
                    + "if it really is one).");
                continue;
            }

            replays.Add(new PlannedReplay(
                file, service, engine, entry?.User, entry?.Databases ?? [],
                chosen.Info.Id, chosen.Planner.DisplayName));
        }

        replays.Sort((a, b) => string.CompareOrdinal(a.Service, b.Service));
        return new RestoreDumpPlan(
            replays, errors, warnings,
            [.. covered.Keys.OrderBy(v => v, StringComparer.Ordinal)]) { CoveredBy = covered };
    }

    /// <summary>
    /// Whether the container is one a Postgres dump may be replayed into: its image says so, or the
    /// operator labelled it as Postgres — the same escape hatch the backup side offers for an image
    /// the detection list does not know.
    /// </summary>
    private static bool IsPostgres(DockerContainerInfo info, BackupContainer planner) =>
        DatabaseDumpTargets.IsPostgresImage(info.Image)
        || (planner.DumpLabel is { } label && PostgresEngines.Contains(label.Trim()));

    /// <summary>
    /// The engine to replay with: what the manifest says, else what the file name implies. A dump the
    /// manifest does not describe is a <c>.sql</c> file or nothing this version can use.
    /// </summary>
    private static DumpEngine? ResolveEngine(string? declared, string file) {
        if (declared is { Length: > 0 }) return PostgresEngines.Contains(declared.Trim()) ? DumpEngine.Postgres : null;
        return file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? DumpEngine.Postgres : null;
    }

    /// <summary>The service a manifest-less dump belongs to: its file name without the extension.</summary>
    private static string ServiceFromFileName(string file) {
        var name = file[(file.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>One <c>dumps[]</c> entry of the manifest, reduced to what the replay needs.</summary>
    private sealed record ManifestDump(
        string Service,
        string? Engine,
        string File,
        string? User,
        IReadOnlyList<string> Databases,
        IReadOnlyList<string> Volumes);

    /// <summary>
    /// Reads the manifest's <c>dumps</c> array. A manifest that cannot be parsed is reported and then
    /// ignored rather than fatal: the files in the archive are still real, and replaying them by name
    /// is better than refusing a restore over metadata.
    /// </summary>
    private static (IReadOnlyList<ManifestDump> Dumps, int FormatVersion, string? Warning) ReadManifest(
        string? manifestJson) {
        if (string.IsNullOrWhiteSpace(manifestJson)) return ([], 1, null);
        JsonObject? root;
        try {
            root = JsonNode.Parse(manifestJson)?.AsObject();
        } catch (JsonException ex) {
            return ([], 1, $"the archive's manifest could not be read ({ex.Message}) — going by the files in it.");
        }
        if (root is null) return ([], 1, "the archive's manifest is not a JSON object — going by the files in it.");

        var version = Number(root["formatVersion"]) ?? 1;
        var dumps = new List<ManifestDump>();
        foreach (var node in root["dumps"] as JsonArray ?? []) {
            if (node is not JsonObject entry) continue;
            var service = Text(entry["service"]);
            var file = Text(entry["file"]);
            if (service is null || file is null) continue; // an entry we cannot act on at all
            dumps.Add(new ManifestDump(
                service, Text(entry["engine"]), file, Text(entry["user"]),
                Strings(entry["databases"]), Strings(entry["volumes"])));
        }
        return (dumps, version, null);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(Text).OfType<string>()] : [];
}
