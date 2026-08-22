using System.Text;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>What a backup run would do with one container of the stack, as the Backups tab shows it.</summary>
public enum BackupServiceAction {
    /// <summary>Stopped for the snapshot (SIGTERM, restarted afterwards).</summary>
    Stop,
    /// <summary>Paused for the snapshot (cgroup freeze, unpaused afterwards — crash-consistent).</summary>
    Pause,
    /// <summary>Left running.</summary>
    Keep,
    /// <summary>Dumped logically (Postgres) while running; its data volume leaves the archive.</summary>
    Dump,
    /// <summary>Excluded: never quiesced, and its volumes are dropped from the archive unless another service mounts them.</summary>
    Excluded,
    /// <summary>Not running right now (or not deployed at all) — nothing to quiesce.</summary>
    NotRunning,
}

/// <summary>
/// One row of the plan preview: a container (or an override whose service is not deployed), what the
/// next run would do with it, why, where that decision came from, and the raw inputs — so the UI can
/// render the label as the read-only source and offer the override where no label is set.
/// </summary>
/// <param name="Service">The compose service; the container name for a container without one.</param>
/// <param name="Container">The container's display name; null for an override without a container.</param>
/// <param name="State">The engine's state (<c>running</c>, <c>exited</c>…); <c>absent</c> for an override without a container.</param>
/// <param name="Volumes">Named volumes the container mounts.</param>
/// <param name="Action">What the run would do.</param>
/// <param name="Reason">Operator-facing prose for the decision.</param>
/// <param name="Source">Where the decision came from.</param>
/// <param name="ExcludeLabel">Raw <c>watchtower.backup.exclude</c> label, or null.</param>
/// <param name="StopLabel">Raw <c>watchtower.backup.stop</c> label, or null.</param>
/// <param name="DumpLabel">Raw <c>watchtower.backup.dump</c> label, or null.</param>
/// <param name="Override">The UI override for the service, or null.</param>
public sealed record BackupServicePreview(
    string Service,
    string? Container,
    string State,
    IReadOnlyList<string> Volumes,
    BackupServiceAction Action,
    string Reason,
    BackupSettingSource Source,
    string? ExcludeLabel,
    string? StopLabel,
    string? DumpLabel,
    BackupServiceOverride? Override);

/// <summary>
/// The dry run of a backup: what the next run would archive, quiesce, dump and skip for the stack as it
/// is deployed right now, row per container, with the planner's warnings. Built from the very same
/// inputs the run uses (<see cref="BackupService"/> prepares both), so the preview never drifts from
/// the run — that is the point: the labels only become discoverable when their effect is visible
/// before 03:30 (ADR-0020).
/// </summary>
/// <param name="Deployed">False when the stack has neither volumes nor containers — nothing to preview.</param>
/// <param name="Volumes">The volumes a run would archive.</param>
/// <param name="ExcludedVolumes">The candidate volumes a run would drop, with why.</param>
/// <param name="Services">One row per container, plus one per override whose service is not deployed; ordered by service then container.</param>
/// <param name="Warnings">The planner's and dump policy's warnings, <c>WARNING: </c> prefix stripped.</param>
/// <param name="LabelSnippet">The UI overrides rendered as compose labels to paste, or null when there are none.</param>
public sealed record BackupPlanPreview(
    bool Deployed,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ExcludedBackupVolume> ExcludedVolumes,
    IReadOnlyList<BackupServicePreview> Services,
    IReadOnlyList<string> Warnings,
    string? LabelSnippet) {

    /// <summary>Assembles the preview from the run's prepared inputs. Pure.</summary>
    /// <param name="containers">Every container of the project, as the planner saw them (overrides attached).</param>
    /// <param name="plan">The plan the run would execute.</param>
    /// <param name="dumpTargets">The databases the run would dump.</param>
    /// <param name="overrides">The stack's UI overrides by service name.</param>
    /// <param name="dumpWarnings">Lines the dump policy logged while selecting targets (any prefix).</param>
    /// <param name="stopContainers">The stack's master switch, for the wording of the keep rows.</param>
    /// <param name="quiesceMode">The stack's default quiesce mode, for the wording of unlabelled rows.</param>
    public static BackupPlanPreview Build(
        IReadOnlyList<BackupContainer> containers,
        BackupPlan plan,
        IReadOnlyList<DumpTarget> dumpTargets,
        IReadOnlyDictionary<string, BackupServiceOverride> overrides,
        IReadOnlyList<string> dumpWarnings,
        bool stopContainers,
        BackupQuiesceMode quiesceMode) {
        var quiesced = plan.Quiesce.ToDictionary(s => s.Container.Id, StringComparer.Ordinal);
        var kept = plan.Keep.ToDictionary(k => k.Container.Id, StringComparer.Ordinal);
        var dumped = dumpTargets.ToDictionary(t => t.ContainerId, StringComparer.Ordinal);
        var planned = new HashSet<string>(plan.Volumes, StringComparer.Ordinal);

        var rows = new List<BackupServicePreview>();
        foreach (var c in containers) {
            var (action, reason, source) = Describe(c, quiesced, kept, dumped, planned, stopContainers, quiesceMode);
            rows.Add(new BackupServicePreview(
                c.Service ?? c.DisplayName, c.DisplayName, c.IsRunning ? "running" : "not running",
                c.VolumeNames, action, reason, source, c.ExcludeLabel, c.StopLabel, c.DumpLabel, c.Override));
        }
        // An override for a service that is not deployed right now still exists and still counts the
        // moment the service comes back — shown so it can be found and removed.
        var present = new HashSet<string>(containers.Select(c => c.Service).OfType<string>(), StringComparer.Ordinal);
        foreach (var (service, o) in overrides.Where(kv => !present.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            rows.Add(new BackupServicePreview(
                service, null, "absent", [], BackupServiceAction.NotRunning,
                "no container of this service is deployed — the override applies once there is one",
                BackupSettingSource.Override, null, null, null, o));

        rows.Sort((a, b) => {
            var byService = string.CompareOrdinal(a.Service, b.Service);
            return byService != 0 ? byService : string.CompareOrdinal(a.Container, b.Container);
        });

        var warnings = plan.Warnings
            .Concat(dumpWarnings.Select(w => w.StartsWith("WARNING: ", StringComparison.Ordinal) ? w["WARNING: ".Length..] : w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new BackupPlanPreview(
            containers.Count > 0 || plan.Volumes.Count > 0 || plan.Excluded.Count > 0,
            plan.Volumes, plan.Excluded, rows, warnings, ComposeLabelSnippet.Render(overrides));
    }

    /// <summary>The action/reason/source of one container, from the plan's and the dump policy's view of it.</summary>
    private static (BackupServiceAction Action, string Reason, BackupSettingSource Source) Describe(
        BackupContainer c,
        IReadOnlyDictionary<string, BackupQuiesceStep> quiesced,
        IReadOnlyDictionary<string, KeptBackupContainer> kept,
        IReadOnlyDictionary<string, DumpTarget> dumped,
        HashSet<string> planned,
        bool stopContainers,
        BackupQuiesceMode quiesceMode) {
        var mounts = c.VolumeNames.Where(planned.Contains).ToList();
        var mountsText = mounts.Count == 0 ? "mounts no archived volume" : $"mounts {string.Join(", ", mounts)}";

        if (!c.IsRunning) {
            var excluded = bool.TryParse(c.Exclude.Value, out var ex) && ex;
            return excluded
                ? (BackupServiceAction.Excluded, $"excluded by {Origin(c.Exclude, BackupPlan.ExcludeLabel)}; not running anyway", c.Exclude.Source)
                : (BackupServiceAction.NotRunning,
                    mounts.Count == 0 ? "not running — nothing to quiesce"
                        : $"not running — nothing to quiesce; {mountsText}, archived as it is on disk",
                    BackupSettingSource.Default);
        }

        if (dumped.TryGetValue(c.Id, out var dump)) {
            var note = dump.DataVolume is { } data
                ? $"dumped with pg_dumpall while running; volume {data} leaves the archive in favour of the dump"
                : "dumped with pg_dumpall while running; its data directory is not a named volume";
            var source = c.Dump.Value is not null ? c.Dump.Source : BackupSettingSource.Default;
            return (BackupServiceAction.Dump,
                c.Dump.Value is not null ? $"{note} ({Origin(c.Dump, BackupPlan.DumpLabel)})" : $"Postgres image — {note}",
                source);
        }

        if (quiesced.TryGetValue(c.Id, out var step)) {
            var verb = step.Mode == BackupQuiesceMode.Pause ? "paused" : "stopped";
            var consistency = step.Mode == BackupQuiesceMode.Pause ? " (crash-consistent)" : "";
            var why = step.Source switch {
                BackupSettingSource.Label => $"by {BackupPlan.StopLabel}={c.Stop.Value} label",
                BackupSettingSource.Override => $"by UI override stop={c.Stop.Value}",
                _ => $"{mountsText} — stack default {(quiesceMode == BackupQuiesceMode.Pause ? "pause" : "stop")}",
            };
            return (step.Mode == BackupQuiesceMode.Pause ? BackupServiceAction.Pause : BackupServiceAction.Stop,
                $"{verb} for the snapshot{consistency}: {why}", step.Source);
        }

        if (kept.TryGetValue(c.Id, out var keep)) {
            return keep.Reason switch {
                BackupKeepReason.Excluded => (BackupServiceAction.Excluded,
                    $"excluded by {Origin(c.Exclude, BackupPlan.ExcludeLabel)} — never quiesced; its volumes are archived only if another service mounts them",
                    keep.Source),
                BackupKeepReason.StopLabel => (BackupServiceAction.Keep,
                    $"kept running by {Origin(c.Stop, BackupPlan.StopLabel)}"
                    + (keep.MountsPlannedVolume ? $" although it {mountsText} — that snapshot is only crash-consistent" : ""),
                    keep.Source),
                BackupKeepReason.CallerRequested => (BackupServiceAction.Keep, "kept running at the run's request", BackupSettingSource.Default),
                BackupKeepReason.MasterSwitchOff => (BackupServiceAction.Keep,
                    "kept running — the stack's \"stop stateful containers\" switch is off"
                    + (keep.MountsPlannedVolume ? $"; it {mountsText}, so that snapshot is only crash-consistent" : ""),
                    BackupSettingSource.Default),
                _ => (BackupServiceAction.Keep, $"kept running — {mountsText}", BackupSettingSource.Default),
            };
        }

        // Running, but neither planned nor kept: only reachable with a master switch that is off and
        // no keep row, which the planner does not produce — described defensively anyway.
        return (BackupServiceAction.Keep,
            stopContainers ? $"kept running — {mountsText}" : "kept running — the stack's \"stop stateful containers\" switch is off",
            BackupSettingSource.Default);
    }

    /// <summary>"label watchtower.backup.x=y" or "UI override x=y" — the source in words.</summary>
    private static string Origin(BackupSetting setting, string label) =>
        setting.Source == BackupSettingSource.Override
            ? $"UI override {label[(label.LastIndexOf('.') + 1)..]}={setting.Value}"
            : $"label {label}={setting.Value}";
}

/// <summary>
/// Renders UI overrides as the compose labels they stand in for — the bridge from "configured here" to
/// "versioned with the stack" (ADR-0020). Byte-for-byte the values the planner would read back, so
/// pasting the snippet and deleting the overrides changes nothing about the next run.
/// </summary>
public static class ComposeLabelSnippet {
    /// <summary>The snippet, or null when there is nothing to render.</summary>
    public static string? Render(IReadOnlyDictionary<string, BackupServiceOverride> overrides) {
        var services = overrides.Where(kv => !kv.Value.IsEmpty).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (services.Count == 0) return null;
        var sb = new StringBuilder("services:\n");
        foreach (var (service, o) in services) {
            sb.Append("  ").Append(service).Append(":\n    labels:\n");
            if (o.Exclude) sb.Append("      ").Append(BackupPlan.ExcludeLabel).Append(": \"true\"\n");
            if (o.Stop is { } stop) sb.Append("      ").Append(BackupPlan.StopLabel).Append(": \"").Append(stop).Append("\"\n");
            if (o.Dump is { } dump) sb.Append("      ").Append(BackupPlan.DumpLabel).Append(": \"").Append(dump).Append("\"\n");
        }
        return sb.ToString().TrimEnd('\n');
    }
}
