using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Per-service settings from two surfaces (ADR-0020): compose labels (infrastructure as code) and UI
/// overrides. The contract under test is precedence — a label always wins, an override only fills a
/// gap — plus the bookkeeping that makes the two discoverable: every decision carries its source, the
/// preview describes each container the way the run will treat it, and the snippet renders overrides
/// as the labels they stand in for.
/// </summary>
public sealed class BackupPlanOverrideTests {

    // ── Builders ─────────────────────────────────────────────────────────────

    private static BackupContainer C(
        string name,
        string[]? volumes = null,
        string? exclude = null,
        string? stop = null,
        string? dump = null,
        BackupServiceOverride? over = null,
        bool running = true) =>
        new($"{name}-id", name, name, 1, running, volumes ?? [], [], exclude, stop, dump, over);

    private static BackupPlan Plan(
        IReadOnlyList<BackupContainer> containers, string[] volumes,
        BackupQuiesceMode mode = BackupQuiesceMode.Stop, bool forceStop = false) =>
        BackupPlan.Create(new BackupPlanRequest(
            containers, volumes, StopContainers: true, QuiesceMode: mode, ForceStop: forceStop));

    private static BackupQuiesceStep Step(BackupPlan plan, string name) =>
        plan.Quiesce.Single(s => s.Container.DisplayName == name);

    private static KeptBackupContainer Kept(BackupPlan plan, string name) =>
        plan.Keep.Single(k => k.Container.DisplayName == name);

    // ── Precedence ───────────────────────────────────────────────────────────

    [Fact]
    public void AnOverrideFillsInForAnAbsentLabel_AndIsReportedAsTheSource() {
        var plan = Plan(
            [C("uploads", ["files"], over: new BackupServiceOverride(Stop: "pause")),
             C("cache", ["redis"], over: new BackupServiceOverride(Exclude: true)),
             C("worker", over: new BackupServiceOverride(Stop: "true"))],
            ["files", "redis"]);

        Assert.Equal(BackupQuiesceMode.Pause, Step(plan, "uploads").Mode);
        Assert.Equal(BackupSettingSource.Override, Step(plan, "uploads").Source);
        Assert.Equal(BackupKeepReason.Excluded, Kept(plan, "cache").Reason);
        Assert.Equal(BackupSettingSource.Override, Kept(plan, "cache").Source);
        Assert.Equal(["files"], plan.Volumes); // redis dropped — the override excludes like the label would
        Assert.Equal(BackupSettingSource.Override, Step(plan, "worker").Source);
    }

    [Fact]
    public void ALabelAlwaysWinsOverAnOverride() {
        var plan = Plan(
            [C("uploads", ["files"], stop: "false", over: new BackupServiceOverride(Stop: "pause")),
             C("db", ["pgdata"], stop: "true", over: new BackupServiceOverride(Exclude: false, Stop: "false"))],
            ["files", "pgdata"]);

        // The label's "false" keeps uploads up; the override's "pause" is shadowed.
        Assert.Equal(BackupKeepReason.StopLabel, Kept(plan, "uploads").Reason);
        Assert.Equal(BackupSettingSource.Label, Kept(plan, "uploads").Source);
        // The label's "true" stops db; the override's "false" is shadowed.
        Assert.Equal(BackupQuiesceMode.Stop, Step(plan, "db").Mode);
        Assert.Equal(BackupSettingSource.Label, Step(plan, "db").Source);
    }

    [Fact]
    public void LabelsAndOverridesAreResolvedPerKnob_NotPerService() {
        // The stop label is set, the exclude is not: the override's exclude still applies.
        var plan = Plan(
            [C("cache", ["redis"], stop: "true", over: new BackupServiceOverride(Exclude: true, Stop: "pause"))],
            ["redis"]);

        var kept = Kept(plan, "cache");
        Assert.Equal(BackupKeepReason.Excluded, kept.Reason);
        Assert.Equal(BackupSettingSource.Override, kept.Source);
        Assert.Empty(plan.Volumes);
    }

    [Fact]
    public void TheEffectiveSettingsOnAContainerSayWhereTheyComeFrom() {
        var c = C("svc", stop: "true", over: new BackupServiceOverride(Exclude: true, Stop: "pause", Dump: "false"));

        Assert.Equal(new BackupSetting("true", BackupSettingSource.Label), c.Stop);
        Assert.Equal(new BackupSetting("true", BackupSettingSource.Override), c.Exclude);
        Assert.Equal(new BackupSetting("false", BackupSettingSource.Override), c.Dump);
        Assert.Equal(new BackupSetting(null, BackupSettingSource.Default), C("plain").Stop);
    }

    [Fact]
    public void MountRuleAndStackDefaultReportTheDefaultSource() {
        var plan = Plan([C("db", ["pgdata"]), C("web")], ["pgdata"], BackupQuiesceMode.Pause);

        Assert.Equal(BackupSettingSource.Default, Step(plan, "db").Source);
        Assert.Equal(BackupQuiesceMode.Pause, Step(plan, "db").Mode);
        Assert.Equal(BackupSettingSource.Default, Kept(plan, "web").Source);
    }

    [Fact]
    public void AForcedStopKeepsTheSourceThatSelectedTheContainer() {
        var plan = Plan([C("uploads", ["files"], stop: "pause")], ["files"], forceStop: true);

        Assert.Equal(BackupQuiesceMode.Stop, Step(plan, "uploads").Mode);
        Assert.Equal(BackupSettingSource.Label, Step(plan, "uploads").Source);
    }

    [Fact]
    public void FromDockerAttachesTheOverrideOfTheContainersService() {
        var overrides = new Dictionary<string, BackupServiceOverride>(StringComparer.Ordinal) {
            ["api"] = new(Stop: "pause"),
        };
        var info = new DockerContainerInfo {
            Id = "abc", Names = ["/stack-api-1"], Image = "img", State = "running", Status = "Up",
            Labels = new() { ["com.docker.compose.service"] = "api" },
        };

        Assert.Equal("pause", BackupContainer.FromDocker(info, overrides).Override?.Stop);
        Assert.Null(BackupContainer.FromDocker(info).Override);
        Assert.Null(BackupContainer.FromDocker(info with { Labels = [] }, overrides).Override);
    }

    // ── Dump policy ──────────────────────────────────────────────────────────

    [Fact]
    public void TheDumpOverrideOptsAServiceInOrOut_UnlessALabelSaysOtherwise() {
        var log = new List<string>();
        var containers = new List<DockerContainerInfo> {
            Docker("mysql", "mysql:8", new() { ["com.docker.compose.service"] = "mysql" }),
            Docker("pg", "postgres:16", new() { ["com.docker.compose.service"] = "pg" }),
            Docker("pg2", "postgres:16", new() {
                ["com.docker.compose.service"] = "pg2", ["watchtower.backup.dump"] = "postgres",
            }),
        };
        var overrides = new Dictionary<string, BackupServiceOverride>(StringComparer.Ordinal) {
            ["mysql"] = new(Dump: "postgres"),   // forces a dump for an image detection would skip
            ["pg"] = new(Dump: "false"),         // opts a detected Postgres out
            ["pg2"] = new(Dump: "false"),        // shadowed by the label
        };

        var targets = DatabaseDumpTargets.Select(containers, new Dictionary<string, string?>(), log.Add, overrides);

        Assert.Equal(["mysql", "pg2"], targets.Select(t => t.Service));
        Assert.Contains(log, l => l.Contains("Service 'pg' opted out of dumps (UI override dump=false)"));
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    [Fact]
    public void ThePreviewDescribesEveryContainerTheWayTheRunTreatsIt() {
        var overrides = new Dictionary<string, BackupServiceOverride>(StringComparer.Ordinal) {
            ["uploads"] = new(Stop: "pause"),
            ["ghost"] = new(Exclude: true),
        };
        var containers = new List<BackupContainer> {
            C("web"),
            C("uploads", ["files"], over: overrides["uploads"]),
            C("db", ["pgdata"]),
            C("cache", ["redis"], exclude: "true"),
            C("old", ["olddata"], running: false),
        };
        var plan = Plan(containers, ["files", "olddata", "pgdata", "redis"]);
        var dump = new DumpTarget("db-id", "db", "db", "postgres:16", DumpEngine.Postgres, "pgdata", ["pgdata"]);
        // The run keeps a dumped database up and drops its data volume — mirror that for the preview.
        plan = BackupPlan.Create(new BackupPlanRequest(
            containers, ["files", "olddata", "pgdata", "redis"], true,
            KeepRunningContainerIds: new HashSet<string> { "db-id" },
            ExcludeVolumes: new Dictionary<string, string> { ["pgdata"] = "covered by the 'db' dump" }));

        var preview = BackupPlanPreview.Build(
            containers, plan, [dump], overrides, ["WARNING: something from the dump policy"],
            stopContainers: true, BackupQuiesceMode.Stop);

        Assert.True(preview.Deployed);
        Assert.Equal(["files", "olddata"], preview.Volumes);
        var rows = preview.Services.ToDictionary(r => r.Service);
        Assert.Equal(["cache", "db", "ghost", "old", "uploads", "web"], preview.Services.Select(r => r.Service));

        Assert.Equal(BackupServiceAction.Keep, rows["web"].Action);
        Assert.Equal(BackupSettingSource.Default, rows["web"].Source);

        Assert.Equal(BackupServiceAction.Pause, rows["uploads"].Action);
        Assert.Equal(BackupSettingSource.Override, rows["uploads"].Source);
        Assert.Contains("UI override stop=pause", rows["uploads"].Reason);
        Assert.Contains("crash-consistent", rows["uploads"].Reason);

        Assert.Equal(BackupServiceAction.Dump, rows["db"].Action);
        Assert.Contains("pg_dumpall", rows["db"].Reason);

        Assert.Equal(BackupServiceAction.Excluded, rows["cache"].Action);
        Assert.Equal(BackupSettingSource.Label, rows["cache"].Source);
        Assert.Equal("true", rows["cache"].ExcludeLabel);

        Assert.Equal(BackupServiceAction.NotRunning, rows["old"].Action);
        Assert.Equal("not running", rows["old"].State);
        Assert.Contains("archived as it is on disk", rows["old"].Reason);

        // The override for an undeployed service is listed so it can be found and removed.
        Assert.Equal("absent", rows["ghost"].State);
        Assert.Null(rows["ghost"].Container);
        Assert.True(rows["ghost"].Override?.Exclude);

        Assert.Equal(["something from the dump policy"], preview.Warnings);
        Assert.Contains("watchtower.backup.stop: \"pause\"", preview.LabelSnippet);
    }

    [Fact]
    public void AnUndeployedStackPreviewsAsNotDeployed() {
        var plan = Plan([], []);
        var preview = BackupPlanPreview.Build([], plan, [], new Dictionary<string, BackupServiceOverride>(), [], true, BackupQuiesceMode.Stop);

        Assert.False(preview.Deployed);
        Assert.Empty(preview.Services);
        Assert.Null(preview.LabelSnippet);
    }

    [Fact]
    public void TheMasterSwitchOffIsExplainedPerRow() {
        var containers = new List<BackupContainer> { C("db", ["pgdata"]) };
        var plan = BackupPlan.Create(new BackupPlanRequest(containers, ["pgdata"], StopContainers: false));

        var preview = BackupPlanPreview.Build(containers, plan, [], new Dictionary<string, BackupServiceOverride>(), [], false, BackupQuiesceMode.Stop);

        var row = Assert.Single(preview.Services);
        Assert.Equal(BackupServiceAction.Keep, row.Action);
        Assert.Contains("switch is off", row.Reason);
        Assert.Contains("crash-consistent", row.Reason);
    }

    // ── Snippet ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheSnippetRendersOverridesAsTheLabelsTheyStandInFor() {
        var snippet = ComposeLabelSnippet.Render(new Dictionary<string, BackupServiceOverride>(StringComparer.Ordinal) {
            ["uploads"] = new(Stop: "pause"),
            ["cache"] = new(Exclude: true),
            ["legacy-db"] = new(Stop: "true", Dump: "false"),
            ["empty"] = new(),
        });

        // The literal's newlines are whatever the checkout gave this file (CRLF under
        // core.autocrlf=true); Render's contract is LF, so only the expected side is normalized.
        Assert.Equal("""
            services:
              cache:
                labels:
                  watchtower.backup.exclude: "true"
              legacy-db:
                labels:
                  watchtower.backup.stop: "true"
                  watchtower.backup.dump: "false"
              uploads:
                labels:
                  watchtower.backup.stop: "pause"
            """.TrimEnd().ReplaceLineEndings("\n"), snippet);
    }

    [Fact]
    public void PastingTheSnippetAndClearingTheOverridesChangesNothing() {
        // The promise behind "promote to labels": an override and its label produce the same plan.
        var over = new BackupServiceOverride(Stop: "pause");
        var viaOverride = Plan([C("uploads", ["files"], over: over)], ["files"]);
        var viaLabel = Plan([C("uploads", ["files"], stop: "pause")], ["files"]);

        Assert.Equal(Step(viaOverride, "uploads").Mode, Step(viaLabel, "uploads").Mode);
        Assert.Equal(viaOverride.Volumes, viaLabel.Volumes);
    }

    [Fact]
    public void NoOverridesMeansNoSnippet() =>
        Assert.Null(ComposeLabelSnippet.Render(new Dictionary<string, BackupServiceOverride>()));

    private static DockerContainerInfo Docker(string name, string image, Dictionary<string, string> labels) => new() {
        Id = $"{name}-id", Names = [$"/{name}"], Image = image, State = "running", Status = "Up", Labels = labels,
    };
}
