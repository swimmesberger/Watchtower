using System.Globalization;
using Watchtower.Application.Entities;

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
/// <param name="IsRunning">Whether the engine reported the container as running. Only running containers can be quiesced.</param>
/// <param name="VolumeNames">Distinct named volumes this container mounts, in the engine's order.</param>
/// <param name="DependsOn">Service names this container's service declares a dependency on, in label order.</param>
/// <param name="ExcludeLabel">Raw <c>watchtower.backup.exclude</c> value, or null when absent.</param>
/// <param name="StopLabel">Raw <c>watchtower.backup.stop</c> value, or null when absent.</param>
/// <param name="DumpLabel">
/// Raw <c>watchtower.backup.dump</c> value, or null when absent. Carried but not interpreted here — the
/// database-aware dump policy reads it and hands the planner its decision back as
/// <see cref="BackupPlanRequest.KeepRunningContainerIds"/> / <see cref="BackupPlanRequest.ExcludeVolumes"/>.
/// </param>
/// <param name="Override">
/// The per-service settings configured in Watchtower's UI for this container's service, or null. They
/// fill in for a label that is <em>absent</em> and never beat one that is present (ADR-0020) — read
/// them through <see cref="Exclude"/>, <see cref="Stop"/> and <see cref="Dump"/>, which apply that rule.
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
    string? DumpLabel = null,
    BackupServiceOverride? Override = null) {

    /// <summary>The effective <c>watchtower.backup.exclude</c> value and where it comes from.</summary>
    public BackupSetting Exclude => Resolve(ExcludeLabel, Override?.Exclude == true ? "true" : null, OverrideSource);

    /// <summary>The effective <c>watchtower.backup.stop</c> value and where it comes from.</summary>
    public BackupSetting Stop => Resolve(StopLabel, Override?.Stop, OverrideSource);

    /// <summary>The effective <c>watchtower.backup.dump</c> value and where it comes from.</summary>
    public BackupSetting Dump => Resolve(DumpLabel, Override?.Dump, OverrideSource);

    /// <summary>Whether the attached override was configured on the stack or inherited from its template.</summary>
    private BackupSettingSource OverrideSource =>
        Override?.FromTemplate == true ? BackupSettingSource.Template : BackupSettingSource.Override;

    /// <summary>The label wins; the override fills a gap; otherwise there is no setting at all.</summary>
    private static BackupSetting Resolve(string? label, string? overrideValue, BackupSettingSource overrideSource) =>
        label is not null ? new(label, BackupSettingSource.Label)
        : overrideValue is not null ? new(overrideValue, overrideSource)
        : new(null, BackupSettingSource.Default);

    /// <summary>
    /// Projects a container as the engine listed it. Tolerates a null <c>Labels</c>/<c>Mounts</c> array
    /// and a container without names, both of which the daemon may report.
    /// </summary>
    /// <param name="container">One entry of <c>GET /containers/json</c> for the compose project.</param>
    /// <param name="overrides">
    /// The stack's per-service UI overrides by service name, if any — attached to the container whose
    /// compose service matches.
    /// </param>
    /// <returns>The planner's view of that container.</returns>
    public static BackupContainer FromDocker(
        DockerContainerInfo container, IReadOnlyDictionary<string, BackupServiceOverride>? overrides = null) {
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

        var service = Label(BackupPlan.ComposeServiceLabel) is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        return new BackupContainer(
            container.Id,
            name,
            service,
            int.TryParse(Label(BackupPlan.ComposeContainerNumberLabel), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number) ? number : 1,
            string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase),
            volumes,
            ParseDependsOn(Label(BackupPlan.ComposeDependsOnLabel)),
            Label(BackupPlan.ExcludeLabel),
            Label(BackupPlan.StopLabel),
            Label(BackupPlan.DumpLabel),
            service is not null && overrides is not null && overrides.TryGetValue(service, out var o) ? o : null);

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

/// <summary>Where an effective per-service backup setting came from (ADR-0020).</summary>
public enum BackupSettingSource {
    /// <summary>Nothing was configured for the service — the mount rule / stack default decided.</summary>
    Default,

    /// <summary>A <c>watchtower.backup.*</c> compose label on the service (infrastructure as code).</summary>
    Label,

    /// <summary>A per-service override configured in Watchtower's UI, filling in for an absent label.</summary>
    Override,

    /// <summary>
    /// A per-service override the stack <em>inherited</em> from its template, filling in for an absent
    /// label where the stack itself configured nothing for that service (design.md §"Backups across
    /// tenants"). The fleet's setting, rendered as such so an operator is not left looking for a stack
    /// override that does not exist.
    /// </summary>
    Template,
}

/// <summary>One effective per-service setting: the value (label syntax) and where it came from.</summary>
/// <param name="Value">The value in label syntax (<c>"true"</c>, <c>"pause"</c>, <c>"postgres"</c>…), or null when unset.</param>
/// <param name="Source">Where it came from; <see cref="BackupSettingSource.Default"/> when <paramref name="Value"/> is null.</param>
public sealed record BackupSetting(string? Value, BackupSettingSource Source);

/// <summary>
/// The per-service settings an operator configures in the UI instead of (or before) writing labels —
/// one value per label, in the label's own syntax, so the two surfaces describe the same thing and an
/// override can be promoted to compose labels verbatim. Null means "no override for this setting".
/// </summary>
/// <param name="Exclude">True stands in for <c>watchtower.backup.exclude=true</c>.</param>
/// <param name="Stop"><c>"true"</c>, <c>"false"</c> or <c>"pause"</c>, standing in for <c>watchtower.backup.stop</c>.</param>
/// <param name="Dump"><c>"false"</c> or <c>"postgres"</c>, standing in for <c>watchtower.backup.dump</c>.</param>
/// <param name="FromTemplate">
/// True when this row came from the stack's <see cref="Entities.StackTemplate"/> rather than from the
/// stack itself. Presentation only — it changes no decision, it changes what the preview calls the
/// decision's source, so a tenant's Backups tab says "template policy" instead of pointing at an
/// override the stack does not have.
/// </param>
public sealed record BackupServiceOverride(
    bool Exclude = false, string? Stop = null, string? Dump = null, bool FromTemplate = false) {
    /// <summary>True when nothing is set — such an override is not worth a row.</summary>
    public bool IsEmpty => !Exclude && Stop is null && Dump is null;
}

/// <summary>Why a running container is left up instead of being quiesced for the snapshot.</summary>
public enum BackupKeepReason {
    /// <summary>It mounts none of the volumes being archived, so stopping it would be pure downtime.</summary>
    NoPlannedMount,

    /// <summary>Its <c>watchtower.backup.stop</c> label says <c>false</c>.</summary>
    StopLabel,

    /// <summary>Its <c>watchtower.backup.exclude</c> label says <c>true</c> — an excluded service is never stopped.</summary>
    Excluded,

    /// <summary>The caller asked for it to stay up (a database whose dump is taken while it runs).</summary>
    CallerRequested,

    /// <summary>The stack's "stop containers" switch is off, so nothing is quiesced at all.</summary>
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
/// <param name="Source">
/// Where the keep decision came from: the label or the UI override for
/// <see cref="BackupKeepReason.StopLabel"/> and <see cref="BackupKeepReason.Excluded"/>,
/// <see cref="BackupSettingSource.Default"/> for every other reason.
/// </param>
public sealed record KeptBackupContainer(
    BackupContainer Container, BackupKeepReason Reason, bool MountsPlannedVolume,
    BackupSettingSource Source = BackupSettingSource.Default);

/// <summary>One container the run takes out of service for the snapshot, and how.</summary>
/// <param name="Container">The container.</param>
/// <param name="Mode">
/// <see cref="BackupQuiesceMode.Stop"/> (SIGTERM, restart afterwards) or
/// <see cref="BackupQuiesceMode.Pause"/> (cgroup freeze, unpause afterwards — crash-consistent).
/// </param>
/// <param name="Source">
/// Where the decision came from: the label or the UI override when one selected the container or its
/// mode, <see cref="BackupSettingSource.Default"/> when the mount rule and the stack default did.
/// </param>
public sealed record BackupQuiesceStep(
    BackupContainer Container, BackupQuiesceMode Mode, BackupSettingSource Source = BackupSettingSource.Default);

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
/// Quiesce every running container, not only the ones that mount a planned volume — what a restore
/// needs while it replays a database dump: a stateless service that merely talks to the database
/// would otherwise reconnect between the session terminate and <c>DROP DATABASE</c>, and
/// <c>--clean</c> would merge into the old database instead of replacing it. Excluded services,
/// caller-kept containers and an explicit <see cref="BackupPlan.StopLabel"/><c>=false</c> still win.
/// </param>
/// <param name="QuiesceMode">
/// The stack's default for containers the mount rule (or <see cref="StopAllRunning"/>) selects and
/// that carry no explicit <see cref="BackupPlan.StopLabel"/> value. <c>stop: true</c> always stops
/// and <c>stop: pause</c> always pauses, whatever this says.
/// </param>
/// <param name="ForceStop">
/// Every quiesced container is <em>stopped</em>, whatever the stack default or its label says — a
/// restore extracts into the volumes, and a paused process resuming over replaced files is no
/// better than a running one. A <c>stop: pause</c> label then still means "quiesce it", just by stopping.
/// </param>
public sealed record BackupPlanRequest(
    IReadOnlyList<BackupContainer> Containers,
    IReadOnlyList<string> Volumes,
    bool StopContainers,
    IReadOnlySet<string>? KeepRunningContainerIds = null,
    IReadOnlyDictionary<string, string>? ExcludeVolumes = null,
    bool StopAllRunning = false,
    BackupQuiesceMode QuiesceMode = BackupQuiesceMode.Stop,
    bool ForceStop = false);

/// <summary>
/// Which volumes one backup (or restore) run touches and which containers it quiesces for them, computed
/// by <see cref="Create(BackupPlanRequest)"/>. Pure: it performs no I/O and holds no engine handle, so
/// every decision the run makes is testable without a daemon.
/// </summary>
/// <remarks>
/// <para>
/// The rule that replaced "stop everything" is <em>mount scoping</em>: a container is quiesced when it
/// mounts a volume this run is about to read or overwrite. For the usual stateless-api + frontend +
/// database stack that is the database alone, so the stateless tier keeps serving traffic through the
/// snapshot. Two per-service compose labels override the decision where the author knows better —
/// <see cref="ExcludeLabel"/> drops a service's volumes from the run entirely and never stops it, and
/// <see cref="StopLabel"/> forces the decision either way (<c>true</c> stops, <c>false</c> keeps,
/// <c>pause</c> freezes instead of stopping).
/// </para>
/// <para>
/// Quiescing follows Compose's own <c>depends_on</c> graph: dependents go down first and dependencies
/// come back first, so an api is never left talking to a database that is already down (or not yet
/// up). The set is grouped into <see cref="Levels"/> — containers with no ordering constraint between
/// them — so the executor can take a whole level down at once and shorten the window to the slowest
/// container of each level rather than the sum. When no dependency is declared, everything is one
/// level; a cycle cannot be ordered at all, so each container becomes its own level in the engine's
/// listing order, which is exactly what the pre-label implementation did.
/// </para>
/// </remarks>
/// <param name="Volumes">The volumes the run touches, sorted <see cref="StringComparer.Ordinal"/>.</param>
/// <param name="Excluded">The candidate volumes that were dropped, sorted by name.</param>
/// <param name="Levels">
/// The containers to quiesce, grouped by dependency level in the order to take them down: every
/// container of level <c>i</c> may go down concurrently, and only once level <c>i</c> is down may
/// level <c>i+1</c> follow. Resuming runs the levels in reverse.
/// </param>
/// <param name="Keep">The running containers left up, in the engine's order.</param>
/// <param name="Warnings">
/// Operator-facing lines, deterministic and free of the "WARNING: " prefix — the caller owns how it
/// marks them up in the run output.
/// </param>
public sealed record BackupPlan(
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ExcludedBackupVolume> Excluded,
    IReadOnlyList<IReadOnlyList<BackupQuiesceStep>> Levels,
    IReadOnlyList<KeptBackupContainer> Keep,
    IReadOnlyList<string> Warnings) {

    /// <summary>Per-service label dropping the service's volumes from the archive and never stopping it.</summary>
    public const string ExcludeLabel = "watchtower.backup.exclude";

    /// <summary>
    /// Per-service label overriding the mount-based decision: <c>"true"</c> (stop), <c>"false"</c> (keep
    /// running) or <c>"pause"</c> (freeze instead of stopping; crash-consistent).
    /// </summary>
    public const string StopLabel = "watchtower.backup.stop";

    /// <summary>The <see cref="StopLabel"/> value selecting <see cref="BackupQuiesceMode.Pause"/>.</summary>
    public const string StopLabelPause = "pause";

    /// <summary>Per-service label opting a database service in or out of a logical dump.</summary>
    public const string DumpLabel = "watchtower.backup.dump";

    /// <summary>Compose's service-name label.</summary>
    public const string ComposeServiceLabel = "com.docker.compose.service";

    /// <summary>Compose's dependency label, see <see cref="BackupContainer.ParseDependsOn"/>.</summary>
    public const string ComposeDependsOnLabel = "com.docker.compose.depends_on";

    /// <summary>Compose's replica-number label.</summary>
    public const string ComposeContainerNumberLabel = "com.docker.compose.container-number";

    /// <summary>
    /// The full quiesce set flattened — <see cref="Levels"/> in order — i.e. a valid sequential
    /// take-down order: dependents first, dependencies last.
    /// </summary>
    public IReadOnlyList<BackupQuiesceStep> Quiesce { get; } = [.. Levels.SelectMany(level => level)];

    /// <summary>
    /// The full quiesce set in resume order — dependencies first. A caller that actually took down only
    /// a prefix of <see cref="Levels"/> must reverse what it took down instead, so it never starts a
    /// container it did not stop.
    /// </summary>
    public IReadOnlyList<BackupQuiesceStep> ResumeOrder => [.. Quiesce.Reverse()];

    /// <summary>Convenience overload projecting the engine's container list through <see cref="BackupContainer.FromDocker"/>.</summary>
    /// <param name="containers">Every container of the compose project, any state, engine order.</param>
    /// <param name="volumes">The candidate volumes.</param>
    /// <param name="stopContainers">The stack's "stop containers" master switch.</param>
    /// <param name="keepRunning">Container ids the caller needs left running.</param>
    /// <param name="excludeVolumes">Volume name → reason detail for volumes captured another way.</param>
    /// <param name="stopAllRunning">Quiesce every running container, not only the volume writers (restore with dumps).</param>
    /// <param name="quiesceMode">The stack's default quiesce mode for unlabelled containers.</param>
    /// <param name="forceStop">Stop everything that is quiesced, labels and default notwithstanding (restore).</param>
    /// <param name="overrides">The stack's per-service UI overrides by service name (ADR-0020).</param>
    /// <returns>The plan.</returns>
    public static BackupPlan Create(
        IReadOnlyList<DockerContainerInfo> containers,
        IReadOnlyList<string> volumes,
        bool stopContainers,
        IReadOnlySet<string>? keepRunning = null,
        IReadOnlyDictionary<string, string>? excludeVolumes = null,
        bool stopAllRunning = false,
        BackupQuiesceMode quiesceMode = BackupQuiesceMode.Stop,
        bool forceStop = false,
        IReadOnlyDictionary<string, BackupServiceOverride>? overrides = null) =>
        Create(new BackupPlanRequest(
            [.. containers.Select(c => BackupContainer.FromDocker(c, overrides))], volumes, stopContainers,
            keepRunning, excludeVolumes, stopAllRunning, quiesceMode, forceStop));

    /// <summary>Applies the mount-scoping, label and ordering rules to one run's inputs.</summary>
    /// <remarks>
    /// The quiesce decision, first match wins:
    /// <list type="number">
    /// <item>the master switch is off — nothing is touched, and <see cref="StopLabel"/><c>=true</c> does
    /// not override it, because the switch is the operator's "never touch my containers";</item>
    /// <item>the service is excluded — an excluded service is outside the run entirely;</item>
    /// <item>the caller needs it running (its data is dumped rather than snapshotted);</item>
    /// <item><see cref="StopLabel"/><c>=false</c> — an explicit "this one tolerates a hot snapshot";</item>
    /// <item><see cref="StopLabel"/><c>=true</c> / <c>=pause</c> — an explicit stop (or pause) even for a
    /// service that mounts nothing;</item>
    /// <item>the caller asked for every running container (<see cref="BackupPlanRequest.StopAllRunning"/>);</item>
    /// <item>it mounts one of the volumes being archived;</item>
    /// <item>otherwise it is left running.</item>
    /// </list>
    /// A quiesced container is stopped or paused: the label decides where it says so, the stack's
    /// <see cref="BackupPlanRequest.QuiesceMode"/> otherwise, and <see cref="BackupPlanRequest.ForceStop"/>
    /// overrides both. A label value that is none of the recognised words is reported and treated as
    /// absent rather than guessed at — the same reasoning as <see cref="EnvInjectionPlan"/>: both guesses
    /// are wrong in a way the operator cannot see from the outside.
    /// </remarks>
    /// <param name="request">The project's containers, the candidate volumes and the caller's overrides.</param>
    /// <returns>The plan, deterministic for any input order.</returns>
    public static BackupPlan Create(BackupPlanRequest request) {
        var labelWarnings = new SortedSet<string>(StringComparer.Ordinal);
        var excluded = new Dictionary<string, bool>(StringComparer.Ordinal);
        var stopLabels = new Dictionary<string, StopDirective>(StringComparer.Ordinal);
        foreach (var container in request.Containers) {
            // The effective value: the label where present, else the UI override (ADR-0020).
            if (ParseLabel(container, ExcludeLabel, container.Exclude.Value, labelWarnings) is { } exclude)
                excluded[container.Id] = exclude;
            if (ParseStopLabel(container, container.Stop.Value, labelWarnings) is { } directive)
                stopLabels[container.Id] = directive;
        }

        var policyWarnings = new SortedSet<string>(StringComparer.Ordinal);
        var (volumes, exclusions) = ResolveVolumes(request, excluded, policyWarnings);
        var planned = new HashSet<string>(volumes, StringComparer.Ordinal);

        var quiesce = new List<BackupQuiesceStep>();
        var keep = new List<KeptBackupContainer>();
        foreach (var container in request.Containers) {
            if (!container.IsRunning) continue;
            var isExcluded = excluded.GetValueOrDefault(container.Id);
            var mountsPlanned = container.VolumeNames.Any(planned.Contains);
            var directive = stopLabels.TryGetValue(container.Id, out var labelled) ? labelled : (StopDirective?)null;
            var reason = KeepReason(request, container, isExcluded, directive, mountsPlanned);
            if (reason is { } kept) {
                if (kept == BackupKeepReason.Excluded && directive is StopDirective.Stop or StopDirective.Pause)
                    policyWarnings.Add(
                        $"{Describe(container)} is labelled both {ExcludeLabel}=true and {StopLabel}="
                        + $"{(directive == StopDirective.Pause ? StopLabelPause : "true")} "
                        + "— the exclusion wins and it is left running.");
                var source = kept switch {
                    BackupKeepReason.Excluded => container.Exclude.Source,
                    BackupKeepReason.StopLabel => container.Stop.Source,
                    _ => BackupSettingSource.Default,
                };
                keep.Add(new KeptBackupContainer(container, kept, mountsPlanned, source));
            } else {
                var (mode, source) = ModeFor(request, container, directive);
                quiesce.Add(new BackupQuiesceStep(container, mode, source));
            }
        }

        var orderWarnings = new List<string>();
        var levels = OrderForQuiesce(quiesce, orderWarnings);

        return new BackupPlan(
            volumes, exclusions, levels, keep,
            [.. labelWarnings, .. policyWarnings, .. orderWarnings]);
    }

    /// <summary>What a <see cref="StopLabel"/> value asks for.</summary>
    private enum StopDirective { Keep, Stop, Pause }

    /// <summary>The quiesce rule, returning null when the container is to be quiesced.</summary>
    private static BackupKeepReason? KeepReason(
        BackupPlanRequest request,
        BackupContainer container,
        bool isExcluded,
        StopDirective? directive,
        bool mountsPlanned) {
        if (!request.StopContainers) return BackupKeepReason.MasterSwitchOff;
        if (isExcluded) return BackupKeepReason.Excluded;
        if (request.KeepRunningContainerIds?.Contains(container.Id) == true) return BackupKeepReason.CallerRequested;
        if (directive is { } labelled) return labelled == StopDirective.Keep ? BackupKeepReason.StopLabel : null;
        if (request.StopAllRunning) return null;
        return mountsPlanned ? null : BackupKeepReason.NoPlannedMount;
    }

    /// <summary>
    /// How a quiesced container goes down — the label/override where explicit, the stack default
    /// otherwise — and where that came from. A forced stop keeps the source: the label still selected the
    /// container, the restore merely refuses to pause it.
    /// </summary>
    private static (BackupQuiesceMode Mode, BackupSettingSource Source) ModeFor(
        BackupPlanRequest request, BackupContainer container, StopDirective? directive) {
        var source = directive is null ? BackupSettingSource.Default : container.Stop.Source;
        if (request.ForceStop) return (BackupQuiesceMode.Stop, source);
        return directive switch {
            StopDirective.Pause => (BackupQuiesceMode.Pause, source),
            StopDirective.Stop => (BackupQuiesceMode.Stop, source),
            _ => (request.QuiesceMode, source),
        };
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
    /// Orders the quiesce set along Compose's dependency graph and groups it into levels — dependents
    /// first, dependencies last, so the reversed levels are a valid resume order. Edges pointing at
    /// services this run is not quiescing are dropped: they impose no constraint on a container that
    /// stays up.
    /// </summary>
    /// <remarks>
    /// Kahn's algorithm over services, dependency-first, taking the alphabetically smallest ready
    /// service each round so the result depends on the graph rather than on the engine's listing order.
    /// A service's level is its distance from the top of the dependent chain: a service nothing (in the
    /// quiesce set) depends on is level 0, its dependencies level 1, and so on — the longest dependent
    /// path decides, so a service is never taken down before everything that needs it. Within a level
    /// the reversed Kahn order is kept, within one service the highest replica number goes first.
    /// A cycle cannot be ordered at all, so the whole set falls back to the engine's order, one
    /// container per level — the pre-<c>depends_on</c> behaviour, which is wrong for the cycle but no
    /// worse than any alternative. Containers without a compose service sit in level 0 (quiesced first,
    /// resumed last), since nothing can declare a dependency on them.
    /// </remarks>
    private static IReadOnlyList<IReadOnlyList<BackupQuiesceStep>> OrderForQuiesce(
        List<BackupQuiesceStep> quiescing, List<string> warnings) {
        if (quiescing.Count == 0) return [];

        var services = new HashSet<string>(
            quiescing.Select(s => s.Container.Service).OfType<string>(), StringComparer.Ordinal);
        var pending = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var step in quiescing) {
            if (step.Container.Service is not { } service) continue;
            if (!pending.TryGetValue(service, out var deps))
                pending[service] = deps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in step.Container.DependsOn)
                if (services.Contains(dependency) && !string.Equals(dependency, service, StringComparison.Ordinal))
                    deps.Add(dependency);
        }
        // No declared dependency inside the quiesce set: nothing constrains the order, so the whole set
        // is one level, kept in the engine's order.
        if (pending.Values.All(d => d.Count == 0)) return [quiescing];

        // Dependents per service, for the level computation below — before Kahn consumes `pending`.
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (service, deps) in pending)
            foreach (var dependency in deps) {
                if (!dependents.TryGetValue(dependency, out var list))
                    dependents[dependency] = list = [];
                list.Add(service);
            }

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
            return [.. quiescing.Select(step => (IReadOnlyList<BackupQuiesceStep>)[step])];
        }

        // Level = 1 + the deepest level among the dependents; walking the dependency-first order
        // backwards visits every dependent before the service it depends on.
        var level = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = order.Count - 1; i >= 0; i--) {
            var service = order[i];
            var depth = 0;
            if (dependents.TryGetValue(service, out var list))
                foreach (var dependent in list)
                    depth = Math.Max(depth, level[dependent] + 1);
            level[service] = depth;
        }

        // Dependency-first above; quiescing runs the other way round. Within one service the highest
        // replica number goes down first, so replica 1 is the last to stop and the first to come back.
        var ordered = new List<(BackupQuiesceStep Step, int Level)>(quiescing.Count);
        ordered.AddRange(quiescing.Where(s => s.Container.Service is null).Select(s => (s, 0)));
        for (var i = order.Count - 1; i >= 0; i--) {
            var service = order[i];
            ordered.AddRange(quiescing
                .Where(s => string.Equals(s.Container.Service, service, StringComparison.Ordinal))
                .OrderByDescending(s => s.Container.ContainerNumber)
                .ThenByDescending(s => s.Container.DisplayName, StringComparer.Ordinal)
                .Select(s => (s, level[service])));
        }
        return [.. ordered
            .GroupBy(e => e.Level)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<BackupQuiesceStep>)[.. g.Select(e => e.Step)])];
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

    /// <summary>
    /// Reads the <see cref="StopLabel"/>: <c>true</c>/<c>false</c> as <see cref="ParseLabel"/> does, plus
    /// <c>pause</c> (same tolerance for casing and whitespace); anything else is reported and ignored.
    /// </summary>
    private static StopDirective? ParseStopLabel(
        BackupContainer container, string? value, SortedSet<string> warnings) {
        if (value is null) return null;
        if (bool.TryParse(value, out var parsed)) return parsed ? StopDirective.Stop : StopDirective.Keep;
        if (string.Equals(value.Trim(), StopLabelPause, StringComparison.OrdinalIgnoreCase)) return StopDirective.Pause;
        warnings.Add(
            $"{Describe(container)} has an unrecognized {StopLabel} value '{value}' — expected \"true\", "
            + $"\"false\" or \"{StopLabelPause}\"; ignoring it.");
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
