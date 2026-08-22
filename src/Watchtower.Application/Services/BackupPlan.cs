using System.Globalization;

namespace Watchtower.Application.Services;

/// <summary>
/// One container of the stack as the backup planner sees it: what it mounts, what it depends on, and
/// the raw values of the three <c>watchtower.backup.*</c> labels the engine reported for it.
/// </summary>
/// <remarks>
/// Runtime-neutral by design (ADR-0010's seam rule): <see cref="FromDocker"/> is the only place that
/// decodes Compose's label syntax, so the planner itself names no Docker concept.
/// </remarks>
/// <param name="Id">Engine id, used to stop/start the container and to match caller-supplied keep sets.</param>
/// <param name="DisplayName">Operator-facing name for log lines — the container's name, else its short id.</param>
/// <param name="Service">The compose service this container belongs to; null for a container without one.</param>
/// <param name="ContainerNumber">
/// The compose replica number (<c>com.docker.compose.container-number</c>), 1 when absent. Only used to
/// order the replicas of one service deterministically.
/// </param>
/// <param name="IsRunning">Whether the engine reported the container as running. Only running containers can be stopped.</param>
/// <param name="VolumeNames">Distinct named volumes this container mounts, in the engine's order.</param>
/// <param name="DependsOn">Service names this container's service declares a dependency on, in label order.</param>
/// <param name="ExcludeLabel">Raw <c>watchtower.backup.exclude</c> value, or null when absent.</param>
/// <param name="StopLabel">Raw <c>watchtower.backup.stop</c> value, or null when absent.</param>
/// <param name="DumpLabel">
/// Raw <c>watchtower.backup.dump</c> value, or null when absent. Carried but not interpreted here — the
/// database-aware dump policy reads it and hands the planner its decision back as
/// <see cref="BackupPlanRequest.KeepRunningContainerIds"/> / <see cref="BackupPlanRequest.ExcludeVolumes"/>.
/// </param>
public sealed record BackupContainer(
    string Id,
    string DisplayName,
    string? Service,
    int ContainerNumber,
    bool IsRunning,
    IReadOnlyList<string> VolumeNames,
    IReadOnlyList<string> DependsOn,
    string? ExcludeLabel = null,
    string? StopLabel = null,
    string? DumpLabel = null) {

    /// <summary>
    /// Projects a container as the engine listed it. Tolerates a null <c>Labels</c>/<c>Mounts</c> array
    /// and a container without names, both of which the daemon may report.
    /// </summary>
    /// <param name="container">One entry of <c>GET /containers/json</c> for the compose project.</param>
    /// <returns>The planner's view of that container.</returns>
    public static BackupContainer FromDocker(DockerContainerInfo container) {
        var labels = container.Labels;
        var name = container.Names?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.TrimStart('/');
        if (string.IsNullOrEmpty(name))
            name = container.Id.Length > 12 ? container.Id[..12] : container.Id;

        var volumes = new List<string>();
        foreach (var mount in container.Mounts ?? []) {
            if (!string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(mount.Name)) continue; // anonymous volume — not a project volume
            if (!volumes.Contains(mount.Name, StringComparer.Ordinal)) volumes.Add(mount.Name);
        }

        return new BackupContainer(
            container.Id,
            name,
            Label(BackupPlan.ComposeServiceLabel) is { } service && !string.IsNullOrWhiteSpace(service)
                ? service.Trim()
                : null,
            int.TryParse(Label(BackupPlan.ComposeContainerNumberLabel), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number) ? number : 1,
            string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase),
            volumes,
            ParseDependsOn(Label(BackupPlan.ComposeDependsOnLabel)),
            Label(BackupPlan.ExcludeLabel),
            Label(BackupPlan.StopLabel),
            Label(BackupPlan.DumpLabel));

        string? Label(string key) => labels is not null && labels.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Decodes Compose v2's <c>com.docker.compose.depends_on</c> label:
    /// <c>"db:service_healthy:true,cache:service_started:false"</c> → <c>["db", "cache"]</c>. A bare
    /// service name without conditions is accepted too, since the label's shape has varied across
    /// Compose releases and a dependency we fail to read would silently reorder the restart.
    /// </summary>
    /// <param name="label">The raw label value, or null when the container carries none.</param>
    /// <returns>The distinct service names, in label order; empty when there is nothing to read.</returns>
    internal static IReadOnlyList<string> ParseDependsOn(string? label) {
        if (string.IsNullOrWhiteSpace(label)) return [];
        var services = new List<string>();
        foreach (var entry in label.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            var service = (colon < 0 ? entry : entry[..colon]).Trim();
            if (service.Length == 0) continue;
            if (!services.Contains(service, StringComparer.Ordinal)) services.Add(service);
        }
        return services;
    }
}

/// <summary>Why a running container is left up instead of being stopped for the snapshot.</summary>
public enum BackupKeepReason {
    /// <summary>It mounts none of the volumes being archived, so stopping it would be pure downtime.</summary>
    NoPlannedMount,

    /// <summary>Its <c>watchtower.backup.stop</c> label says <c>false</c>.</summary>
    StopLabel,

    /// <summary>Its <c>watchtower.backup.exclude</c> label says <c>true</c> — an excluded service is never stopped.</summary>
    Excluded,

    /// <summary>The caller asked for it to stay up (a database whose dump is taken while it runs).</summary>
    CallerRequested,

    /// <summary>The stack's "stop containers" switch is off, so nothing is stopped at all.</summary>
    MasterSwitchOff,
}

/// <summary>Why a candidate volume is not in the archive.</summary>
public enum BackupVolumeExclusionReason {
    /// <summary>Every service mounting it is labelled <c>watchtower.backup.exclude=true</c>.</summary>
    Label,

    /// <summary>The caller excluded it — its contents are captured another way (a logical dump).</summary>
    Caller,
}

/// <summary>A candidate volume dropped from the run, with the reason to show the operator.</summary>
/// <param name="Name">The volume's name.</param>
/// <param name="Reason">What dropped it.</param>
/// <param name="Detail">
/// Short prose fragment for the log line — the mounting services for
/// <see cref="BackupVolumeExclusionReason.Label"/>, the caller's own wording otherwise.
/// </param>
public sealed record ExcludedBackupVolume(string Name, BackupVolumeExclusionReason Reason, string Detail);

/// <summary>A running container the plan leaves up.</summary>
/// <param name="Container">The container.</param>
/// <param name="Reason">Why it is left up; the first matching rule wins.</param>
/// <param name="MountsPlannedVolume">
/// True when it mounts at least one volume the run does touch — the case worth warning about, because
/// the snapshot of that volume is then only crash-consistent.
/// </param>
public sealed record KeptBackupContainer(
    BackupContainer Container, BackupKeepReason Reason, bool MountsPlannedVolume);

/// <summary>The inputs <see cref="BackupPlan.Create(BackupPlanRequest)"/> decides from.</summary>
/// <param name="Containers">
/// Every container of the compose project, in any state, in the engine's listing order. Non-running
/// ones still count for the volume decision: an excluded service that happens to be down must not
/// suddenly put its volume back into the archive.
/// </param>
/// <param name="Volumes">
/// The candidate volumes — the project's volumes for a backup, the restorable targets for a restore.
/// </param>
/// <param name="StopContainers">The stack's <c>BackupStopContainers</c> master switch.</param>
/// <param name="KeepRunningContainerIds">
/// Containers the caller needs left running (their data is captured by a dump instead of a file
/// snapshot). Ignored for containers that are not running anyway.
/// </param>
/// <param name="ExcludeVolumes">
/// Volume name → reason detail, for volumes the caller captures another way. The label exclusion wins
/// when both apply, because it is the operator's own instruction.
/// </param>
/// <param name="StopAllRunning">
/// Stop every running container, not only the ones that mount a planned volume — what a restore
/// needs while it replays a database dump: a stateless service that merely talks to the database
/// would otherwise reconnect between the session terminate and <c>DROP DATABASE</c>, and
/// <c>--clean</c> would merge into the old database instead of replacing it. Excluded services,
/// caller-kept containers and an explicit <see cref="BackupPlan.StopLabel"/><c>=false</c> still win.
/// </param>
public sealed record BackupPlanRequest(
    IReadOnlyList<BackupContainer> Containers,
    IReadOnlyList<string> Volumes,
    bool StopContainers,
    IReadOnlySet<string>? KeepRunningContainerIds = null,
    IReadOnlyDictionary<string, string>? ExcludeVolumes = null,
    bool StopAllRunning = false);

/// <summary>
/// Which volumes one backup (or restore) run touches and which containers it stops for them, computed
/// by <see cref="Create(BackupPlanRequest)"/>. Pure: it performs no I/O and holds no engine handle, so
/// every decision the run makes is testable without a daemon.
/// </summary>
/// <remarks>
/// <para>
/// The rule that replaced "stop everything" is <em>mount scoping</em>: a container is stopped when it
/// mounts a volume this run is about to read or overwrite. For the usual stateless-api + frontend +
/// database stack that is the database alone, so the stateless tier keeps serving traffic through the
/// snapshot. Two per-service compose labels override the decision where the author knows better —
/// <see cref="ExcludeLabel"/> drops a service's volumes from the run entirely and never stops it, and
/// <see cref="StopLabel"/> forces the stop decision either way.
/// </para>
/// <para>
/// Stops and restarts follow Compose's own <c>depends_on</c> graph: dependents stop first and
/// dependencies restart first, so an api is never left talking to a database that is already down (or
/// not yet up). When no dependency is declared — or the graph has a cycle — the engine's listing order
/// is used unchanged, which is exactly what the pre-label implementation did.
/// </para>
/// </remarks>
/// <param name="Volumes">The volumes the run touches, sorted <see cref="StringComparer.Ordinal"/>.</param>
/// <param name="Excluded">The candidate volumes that were dropped, sorted by name.</param>
/// <param name="Stop">The containers to stop, in the order to stop them.</param>
/// <param name="Keep">The running containers left up, in the engine's order.</param>
/// <param name="Warnings">
/// Operator-facing lines, deterministic and free of the "WARNING: " prefix — the caller owns how it
/// marks them up in the run output.
/// </param>
public sealed record BackupPlan(
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ExcludedBackupVolume> Excluded,
    IReadOnlyList<BackupContainer> Stop,
    IReadOnlyList<KeptBackupContainer> Keep,
    IReadOnlyList<string> Warnings) {

    /// <summary>Per-service label dropping the service's volumes from the archive and never stopping it.</summary>
    public const string ExcludeLabel = "watchtower.backup.exclude";

    /// <summary>Per-service label overriding the mount-based stop decision (<c>"true"</c> / <c>"false"</c>).</summary>
    public const string StopLabel = "watchtower.backup.stop";

    /// <summary>Per-service label opting a database service in or out of a logical dump.</summary>
    public const string DumpLabel = "watchtower.backup.dump";

    /// <summary>Compose's service-name label.</summary>
    public const string ComposeServiceLabel = "com.docker.compose.service";

    /// <summary>Compose's dependency label, see <see cref="BackupContainer.ParseDependsOn"/>.</summary>
    public const string ComposeDependsOnLabel = "com.docker.compose.depends_on";

    /// <summary>Compose's replica-number label.</summary>
    public const string ComposeContainerNumberLabel = "com.docker.compose.container-number";

    /// <summary>
    /// The full stop set in restart order — dependencies first. A caller that actually stopped only a
    /// prefix of <see cref="Stop"/> must reverse what it stopped instead, so it never starts a
    /// container it did not take down.
    /// </summary>
    public IReadOnlyList<BackupContainer> RestartOrder => [.. Stop.Reverse()];

    /// <summary>Convenience overload projecting the engine's container list through <see cref="BackupContainer.FromDocker"/>.</summary>
    /// <param name="containers">Every container of the compose project, any state, engine order.</param>
    /// <param name="volumes">The candidate volumes.</param>
    /// <param name="stopContainers">The stack's "stop containers" master switch.</param>
    /// <param name="keepRunning">Container ids the caller needs left running.</param>
    /// <param name="excludeVolumes">Volume name → reason detail for volumes captured another way.</param>
    /// <param name="stopAllRunning">Stop every running container, not only the volume writers (restore with dumps).</param>
    /// <returns>The plan.</returns>
    public static BackupPlan Create(
        IReadOnlyList<DockerContainerInfo> containers,
        IReadOnlyList<string> volumes,
        bool stopContainers,
        IReadOnlySet<string>? keepRunning = null,
        IReadOnlyDictionary<string, string>? excludeVolumes = null,
        bool stopAllRunning = false) =>
        Create(new BackupPlanRequest(
            [.. containers.Select(BackupContainer.FromDocker)], volumes, stopContainers,
            keepRunning, excludeVolumes, stopAllRunning));

    /// <summary>Applies the mount-scoping, label and ordering rules to one run's inputs.</summary>
    /// <remarks>
    /// The stop decision, first match wins:
    /// <list type="number">
    /// <item>the master switch is off — nothing is stopped, and <see cref="StopLabel"/><c>=true</c> does
    /// not override it, because the switch is the operator's "never touch my containers";</item>
    /// <item>the service is excluded — an excluded service is outside the run entirely;</item>
    /// <item>the caller needs it running (its data is dumped rather than snapshotted);</item>
    /// <item><see cref="StopLabel"/><c>=false</c> — an explicit "this one tolerates a hot snapshot";</item>
    /// <item><see cref="StopLabel"/><c>=true</c> — an explicit stop even for a service that mounts nothing;</item>
    /// <item>the caller asked for every running container (<see cref="BackupPlanRequest.StopAllRunning"/>);</item>
    /// <item>it mounts one of the volumes being archived;</item>
    /// <item>otherwise it is left running.</item>
    /// </list>
    /// A label value that is neither <c>"true"</c> nor <c>"false"</c> is reported and treated as absent
    /// rather than guessed at — the same reasoning as <see cref="EnvInjectionPlan"/>: both guesses are
    /// wrong in a way the operator cannot see from the outside.
    /// </remarks>
    /// <param name="request">The project's containers, the candidate volumes and the caller's overrides.</param>
    /// <returns>The plan, deterministic for any input order.</returns>
    public static BackupPlan Create(BackupPlanRequest request) {
        var labelWarnings = new SortedSet<string>(StringComparer.Ordinal);
        var excluded = new Dictionary<string, bool>(StringComparer.Ordinal);
        var stopLabels = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var container in request.Containers) {
            if (ParseLabel(container, ExcludeLabel, container.ExcludeLabel, labelWarnings) is { } exclude)
                excluded[container.Id] = exclude;
            if (ParseLabel(container, StopLabel, container.StopLabel, labelWarnings) is { } stopping)
                stopLabels[container.Id] = stopping;
        }

        var policyWarnings = new SortedSet<string>(StringComparer.Ordinal);
        var (volumes, exclusions) = ResolveVolumes(request, excluded, policyWarnings);
        var planned = new HashSet<string>(volumes, StringComparer.Ordinal);

        var stop = new List<BackupContainer>();
        var keep = new List<KeptBackupContainer>();
        foreach (var container in request.Containers) {
            if (!container.IsRunning) continue;
            var isExcluded = excluded.GetValueOrDefault(container.Id);
            var mountsPlanned = container.VolumeNames.Any(planned.Contains);
            var reason = KeepReason(request, container, isExcluded, stopLabels, mountsPlanned);
            if (reason is { } kept) {
                if (kept == BackupKeepReason.Excluded && stopLabels.GetValueOrDefault(container.Id))
                    policyWarnings.Add(
                        $"{Describe(container)} is labelled both {ExcludeLabel}=true and {StopLabel}=true "
                        + "— the exclusion wins and it is left running.");
                keep.Add(new KeptBackupContainer(container, kept, mountsPlanned));
            } else {
                stop.Add(container);
            }
        }

        var orderWarnings = new List<string>();
        var ordered = OrderForStop(stop, orderWarnings);

        return new BackupPlan(
            volumes, exclusions, ordered, keep,
            [.. labelWarnings, .. policyWarnings, .. orderWarnings]);
    }

    /// <summary>The stop rule, returning null when the container is to be stopped.</summary>
    private static BackupKeepReason? KeepReason(
        BackupPlanRequest request,
        BackupContainer container,
        bool isExcluded,
        Dictionary<string, bool> stopLabels,
        bool mountsPlanned) {
        if (!request.StopContainers) return BackupKeepReason.MasterSwitchOff;
        if (isExcluded) return BackupKeepReason.Excluded;
        if (request.KeepRunningContainerIds?.Contains(container.Id) == true) return BackupKeepReason.CallerRequested;
        if (stopLabels.TryGetValue(container.Id, out var stop)) return stop ? null : BackupKeepReason.StopLabel;
        if (request.StopAllRunning) return null;
        return mountsPlanned ? null : BackupKeepReason.NoPlannedMount;
    }

    /// <summary>
    /// Narrows the candidate volumes to the ones this run touches. A volume is dropped only when
    /// <em>every</em> container mounting it is excluded: a volume shared with a service that is not
    /// excluded stays in, because dropping it would silently lose that service's data — the operator is
    /// told instead. A volume nobody mounts stays in, which is what the pre-label implementation did
    /// with every project volume.
    /// </summary>
    private static (IReadOnlyList<string> Volumes, IReadOnlyList<ExcludedBackupVolume> Excluded) ResolveVolumes(
        BackupPlanRequest request, Dictionary<string, bool> excluded, SortedSet<string> warnings) {
        var candidates = new List<string>();
        foreach (var volume in request.Volumes)
            if (!candidates.Contains(volume, StringComparer.Ordinal)) candidates.Add(volume);
        candidates.Sort(StringComparer.Ordinal);

        var kept = new List<string>(candidates.Count);
        var exclusions = new List<ExcludedBackupVolume>();
        foreach (var volume in candidates) {
            var excludedBy = new SortedSet<string>(StringComparer.Ordinal);
            var mountedBy = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var container in request.Containers) {
                if (!container.VolumeNames.Contains(volume, StringComparer.Ordinal)) continue;
                (excluded.GetValueOrDefault(container.Id) ? excludedBy : mountedBy).Add(Identity(container));
            }

            if (excludedBy.Count > 0 && mountedBy.Count == 0) {
                exclusions.Add(new ExcludedBackupVolume(
                    volume, BackupVolumeExclusionReason.Label, ServiceList(excludedBy)));
                continue;
            }
            if (excludedBy.Count > 0)
                warnings.Add(
                    $"volume '{volume}' is mounted by excluded service(s) {string.Join(", ", excludedBy)} and by "
                    + $"{string.Join(", ", mountedBy)} — it is still archived, because dropping it would "
                    + $"silently lose {string.Join(", ", mountedBy)}'s data.");

            if (request.ExcludeVolumes?.TryGetValue(volume, out var detail) == true) {
                exclusions.Add(new ExcludedBackupVolume(volume, BackupVolumeExclusionReason.Caller, detail));
                continue;
            }
            kept.Add(volume);
        }
        return (kept, exclusions);
    }

    /// <summary>
    /// Orders the stop set along Compose's dependency graph — dependents first, dependencies last, so a
    /// reversal of this list is a valid start order. Edges pointing at services this run is not stopping
    /// are dropped: they impose no constraint on a container that stays up.
    /// </summary>
    /// <remarks>
    /// Kahn's algorithm over services, dependency-first, taking the alphabetically smallest ready
    /// service each round so the result depends on the graph rather than on the engine's listing order.
    /// A cycle cannot be ordered at all, so the whole set falls back to the engine's order — the
    /// pre-<c>depends_on</c> behaviour, which is wrong for the cycle but no worse than any alternative.
    /// Containers without a compose service are stopped first and restarted last, since nothing can
    /// declare a dependency on them.
    /// </remarks>
    private static IReadOnlyList<BackupContainer> OrderForStop(
        List<BackupContainer> stopping, List<string> warnings) {
        if (stopping.Count == 0) return [];

        var services = new HashSet<string>(
            stopping.Select(c => c.Service).OfType<string>(), StringComparer.Ordinal);
        var pending = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var container in stopping) {
            if (container.Service is not { } service) continue;
            if (!pending.TryGetValue(service, out var deps))
                pending[service] = deps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in container.DependsOn)
                if (services.Contains(dependency) && !string.Equals(dependency, service, StringComparison.Ordinal))
                    deps.Add(dependency);
        }
        // No declared dependency inside the stop set: keep the engine's order untouched, exactly as
        // before depends_on was consulted at all.
        if (pending.Values.All(d => d.Count == 0)) return stopping;

        var order = new List<string>(pending.Count);
        var ready = new SortedSet<string>(
            pending.Where(p => p.Value.Count == 0).Select(p => p.Key), StringComparer.Ordinal);
        foreach (var service in ready) pending.Remove(service);
        while (ready.Count > 0) {
            var service = ready.Min!;
            ready.Remove(service);
            order.Add(service);
            foreach (var dependent in pending.Where(p => p.Value.Contains(service)).ToList()) {
                dependent.Value.Remove(service);
                if (dependent.Value.Count != 0) continue;
                pending.Remove(dependent.Key);
                ready.Add(dependent.Key);
            }
        }
        if (pending.Count > 0) {
            warnings.Add(
                $"circular depends_on between services {string.Join(", ", pending.Keys.OrderBy(s => s, StringComparer.Ordinal))} "
                + "— falling back to the engine's container order for this run.");
            return stopping;
        }

        // Dependency-first above; stopping runs the other way round. Within one service the highest
        // replica number goes down first, so replica 1 is the last to stop and the first to come back.
        var ordered = new List<BackupContainer>(stopping.Count);
        ordered.AddRange(stopping.Where(c => c.Service is null));
        for (var i = order.Count - 1; i >= 0; i--) {
            var service = order[i];
            ordered.AddRange(stopping
                .Where(c => string.Equals(c.Service, service, StringComparison.Ordinal))
                .OrderByDescending(c => c.ContainerNumber)
                .ThenByDescending(c => c.DisplayName, StringComparer.Ordinal));
        }
        return ordered;
    }

    /// <summary>
    /// Reads one <c>"true"</c>/<c>"false"</c> label, recording a warning for anything else.
    /// <c>bool.TryParse</c> accepts surrounding whitespace and any casing, the right amount of tolerance
    /// for a hand-written YAML label. The warning is keyed by service + label + value, so a service
    /// scaled to five replicas reports its typo once.
    /// </summary>
    private static bool? ParseLabel(
        BackupContainer container, string key, string? value, SortedSet<string> warnings) {
        if (value is null) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        warnings.Add(
            $"{Describe(container)} has an unrecognized {key} value '{value}' — expected \"true\" or "
            + "\"false\"; ignoring it.");
        return null;
    }

    /// <summary>How a container is named in a warning: its service, else the container itself.</summary>
    private static string Describe(BackupContainer container) =>
        container.Service is { } service ? $"service '{service}'" : $"container '{container.DisplayName}'";

    /// <summary>The bare name a container contributes to a list of services — its service, else its own name.</summary>
    private static string Identity(BackupContainer container) => container.Service ?? container.DisplayName;

    /// <summary>Renders names as one prose fragment: <c>service db</c> / <c>services db, cache</c>.</summary>
    private static string ServiceList(IReadOnlyCollection<string> names) =>
        $"service{(names.Count == 1 ? "" : "s")} {string.Join(", ", names)}";
}
