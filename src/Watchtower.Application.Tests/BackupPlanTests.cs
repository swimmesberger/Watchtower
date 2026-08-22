using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The backup planner: which volumes a run archives, which containers it stops for them, and in what
/// order it takes them down. Every decision the backup and restore paths make lives here, so this
/// suite is where the downtime contract is pinned — a stateless service must keep serving, a database
/// must go down before its volume is read, and the restart must never bring an api up before its db.
/// </summary>
public sealed class BackupPlanTests {

    // ── Builders ─────────────────────────────────────────────────────────────

    /// <summary>A running container of the service it is named after, unless told otherwise.</summary>
    private static BackupContainer C(
        string name,
        string? service = null,
        bool running = true,
        string[]? volumes = null,
        string[]? dependsOn = null,
        string? exclude = null,
        string? stop = null,
        int number = 1) =>
        new($"{name}-id", name, service ?? name, number, running, volumes ?? [], dependsOn ?? [], exclude, stop);

    /// <summary>A running container carrying no compose service label at all.</summary>
    private static BackupContainer Orphan(string name, string[]? volumes = null) =>
        new($"{name}-id", name, null, 1, true, volumes ?? [], []);

    private static BackupPlan Plan(
        IReadOnlyList<BackupContainer> containers,
        string[] volumes,
        bool stopContainers = true,
        IReadOnlySet<string>? keepRunning = null,
        IReadOnlyDictionary<string, string>? excludeVolumes = null,
        bool stopAllRunning = false) =>
        BackupPlan.Create(new BackupPlanRequest(
            containers, volumes, stopContainers, keepRunning, excludeVolumes, stopAllRunning));

    private static string[] Names(IEnumerable<BackupContainer> containers) =>
        [.. containers.Select(c => c.DisplayName)];

    // ── Mount scoping ────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheContainersMountingAnArchivedVolumeAreStopped() {
        var plan = Plan(
            [C("web"), C("api", volumes: ["uploads"]), C("db", volumes: ["pgdata"])],
            ["pgdata", "uploads"]);

        Assert.Equal(["api", "db"], Names(plan.Stop));
        var kept = Assert.Single(plan.Keep);
        Assert.Equal("web", kept.Container.DisplayName);
        Assert.Equal(BackupKeepReason.NoPlannedMount, kept.Reason);
        Assert.False(kept.MountsPlannedVolume);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void TheMasterSwitchStopsNothing_NotEvenAServiceLabelledStopTrue() {
        var plan = Plan(
            [C("api", volumes: ["uploads"]), C("cron", stop: "true")],
            ["uploads"],
            stopContainers: false);

        Assert.Empty(plan.Stop);
        Assert.Equal(["api", "cron"], Names(plan.Keep.Select(k => k.Container)));
        Assert.All(plan.Keep, k => Assert.Equal(BackupKeepReason.MasterSwitchOff, k.Reason));
        // The mount is still reported, so the caller can decide whether that is worth a warning.
        Assert.True(plan.Keep[0].MountsPlannedVolume);
        Assert.Equal(["uploads"], plan.Volumes);
    }

    [Fact]
    public void ANonRunningContainerIsNeitherStoppedNorKept_ButStillOwnsItsVolumes() {
        var plan = Plan(
            [C("api", volumes: ["uploads"]), C("db", running: false, volumes: ["pgdata"], exclude: "true")],
            ["pgdata", "uploads"]);

        Assert.Equal(["api"], Names(plan.Stop));
        Assert.Empty(plan.Keep);
        // The exclusion holds even though the excluded service happens to be down right now.
        Assert.Equal(["uploads"], plan.Volumes);
        Assert.Equal("pgdata", Assert.Single(plan.Excluded).Name);
    }

    [Fact]
    public void AVolumeNobodyMountsIsStillArchived() {
        var plan = Plan([C("api", volumes: ["uploads"])], ["orphaned", "uploads"]);

        Assert.Equal(["orphaned", "uploads"], plan.Volumes);
        Assert.Empty(plan.Excluded);
        Assert.Equal(["api"], Names(plan.Stop));
    }

    // ── Labels ───────────────────────────────────────────────────────────────

    [Fact]
    public void ExcludeDropsAVolumeOnlyTheExcludedServiceMounts() {
        var plan = Plan(
            [C("api", volumes: ["uploads"]), C("cache", volumes: ["redis-data"], exclude: "true")],
            ["redis-data", "uploads"]);

        Assert.Equal(["uploads"], plan.Volumes);
        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal("redis-data", excluded.Name);
        Assert.Equal(BackupVolumeExclusionReason.Label, excluded.Reason);
        Assert.Equal("service cache", excluded.Detail);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void AVolumeSharedWithANonExcludedServiceSurvivesTheExclusion() {
        var plan = Plan(
            [C("api", volumes: ["shared"]), C("cache", volumes: ["shared"], exclude: "true")],
            ["shared"]);

        Assert.Equal(["shared"], plan.Volumes);
        Assert.Empty(plan.Excluded);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("volume 'shared' is mounted by excluded service(s) cache and by api", warning);
        Assert.Contains("silently lose api's data", warning);
    }

    [Fact]
    public void AnExcludedServiceIsNeverStopped_EvenWhenItMountsAnArchivedVolume() {
        var plan = Plan(
            [C("api", volumes: ["shared"]), C("cache", volumes: ["shared"], exclude: "true")],
            ["shared"]);

        Assert.Equal(["api"], Names(plan.Stop));
        var kept = Assert.Single(plan.Keep);
        Assert.Equal("cache", kept.Container.DisplayName);
        Assert.Equal(BackupKeepReason.Excluded, kept.Reason);
        Assert.True(kept.MountsPlannedVolume);
    }

    [Fact]
    public void StopFalseKeepsAMounterRunning_AndSaysItMountsAPlannedVolume() {
        var plan = Plan([C("db", volumes: ["pgdata"], stop: "false")], ["pgdata"]);

        Assert.Empty(plan.Stop);
        var kept = Assert.Single(plan.Keep);
        Assert.Equal(BackupKeepReason.StopLabel, kept.Reason);
        Assert.True(kept.MountsPlannedVolume);
        Assert.Equal(["pgdata"], plan.Volumes);
    }

    [Fact]
    public void StopTrueStopsAServiceThatMountsNothing() {
        var plan = Plan([C("worker", stop: "true"), C("web")], ["uploads"]);

        Assert.Equal(["worker"], Names(plan.Stop));
        Assert.Equal(BackupKeepReason.NoPlannedMount, Assert.Single(plan.Keep).Reason);
    }

    [Fact]
    public void ExcludeWinsOverStopTrue_AndSaysSo() {
        var plan = Plan([C("cache", volumes: ["redis-data"], exclude: "true", stop: "true")], ["redis-data"]);

        Assert.Empty(plan.Stop);
        Assert.Equal(BackupKeepReason.Excluded, Assert.Single(plan.Keep).Reason);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("service 'cache' is labelled both watchtower.backup.exclude=true", warning);
        Assert.Contains("the exclusion wins", warning);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("maybe")]
    [InlineData("")]
    public void AnUnrecognizedLabelValueIsReportedOnceAndTreatedAsAbsent(string value) {
        var plan = Plan([C("db", volumes: ["pgdata"], exclude: value)], ["pgdata"]);

        // Absent means the mount decides — the volume is archived and its writer goes down.
        Assert.Equal(["pgdata"], plan.Volumes);
        Assert.Equal(["db"], Names(plan.Stop));
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains($"service 'db' has an unrecognized watchtower.backup.exclude value '{value}'", warning);
        Assert.Contains("expected \"true\" or \"false\"", warning);
    }

    [Theory]
    [InlineData(" TRUE ")]
    [InlineData("False")]
    public void LabelParsingToleratesCasingAndWhitespace(string value) {
        var plan = Plan([C("db", volumes: ["pgdata"], stop: value)], ["pgdata"]);

        Assert.Empty(plan.Warnings);
        Assert.Equal(bool.Parse(value.Trim()) ? 1 : 0, plan.Stop.Count);
    }

    [Fact]
    public void ARepeatedLabelTypoIsReportedOncePerService_NotOncePerReplica() {
        var plan = Plan(
            [C("api-1", service: "api", volumes: ["uploads"], exclude: "maybe", number: 1),
             C("api-2", service: "api", volumes: ["uploads"], exclude: "maybe", number: 2)],
            ["uploads"]);

        Assert.Single(plan.Warnings);
    }

    // ── Caller overrides (the database-dump seam) ────────────────────────────

    [Fact]
    public void StopAllRunningTakesDownNonWritersToo_ButNotTheCallerKeptOrLabelledOnes() {
        // A restore replaying a dump: the api mounts nothing, yet it must not reconnect to the
        // database while --clean drops and recreates it.
        var api = C("api", volumes: []);
        var db = C("db", volumes: ["pgdata"]);
        var web = C("web", volumes: [], stop: "false");
        var cache = C("cache", volumes: ["cachedata"], exclude: "true");
        var plan = Plan([api, db, web, cache], ["uploads"],
            keepRunning: new HashSet<string>(["db-id"]), stopAllRunning: true);

        Assert.Equal(["api"], Names(plan.Stop));
        Assert.Equal(BackupKeepReason.CallerRequested, plan.Keep.Single(k => k.Container.Id == "db-id").Reason);
        Assert.Equal(BackupKeepReason.StopLabel, plan.Keep.Single(k => k.Container.Id == "web-id").Reason);
        Assert.Equal(BackupKeepReason.Excluded, plan.Keep.Single(k => k.Container.Id == "cache-id").Reason);
    }

    [Fact]
    public void StopAllRunningStillHonoursTheMasterSwitch() {
        var plan = Plan([C("api"), C("db", volumes: ["pgdata"])], ["pgdata"],
            stopContainers: false, stopAllRunning: true);
        Assert.Empty(plan.Stop);
        Assert.All(plan.Keep, k => Assert.Equal(BackupKeepReason.MasterSwitchOff, k.Reason));
    }

    [Fact]
    public void ACallerRequestedContainerIsLeftRunningEvenThoughItMounts() {
        var plan = Plan(
            [C("api", volumes: ["uploads"]), C("db", volumes: ["pgdata"])],
            ["pgdata", "uploads"],
            keepRunning: new HashSet<string>(["db-id"], StringComparer.Ordinal));

        Assert.Equal(["api"], Names(plan.Stop));
        var kept = Assert.Single(plan.Keep);
        Assert.Equal(BackupKeepReason.CallerRequested, kept.Reason);
        Assert.True(kept.MountsPlannedVolume);
    }

    [Fact]
    public void ACallerExcludedVolumeIsDroppedWithTheCallersOwnWording() {
        var plan = Plan(
            [C("db", volumes: ["pgdata"])],
            ["pgdata"],
            excludeVolumes: new Dictionary<string, string>(StringComparer.Ordinal) {
                ["pgdata"] = "covered by the 'db' dump",
            });

        Assert.Empty(plan.Volumes);
        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal(BackupVolumeExclusionReason.Caller, excluded.Reason);
        Assert.Equal("covered by the 'db' dump", excluded.Detail);
        // Nothing left to archive, so nothing worth stopping either.
        Assert.Empty(plan.Stop);
        Assert.Equal(BackupKeepReason.NoPlannedMount, Assert.Single(plan.Keep).Reason);
    }

    [Fact]
    public void TheLabelWinsWhenTheCallerExcludesTheSameVolume() {
        var plan = Plan(
            [C("db", volumes: ["pgdata"], exclude: "true")],
            ["pgdata"],
            excludeVolumes: new Dictionary<string, string>(StringComparer.Ordinal) {
                ["pgdata"] = "covered by the 'db' dump",
            });

        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal(BackupVolumeExclusionReason.Label, excluded.Reason);
        Assert.Equal("service db", excluded.Detail);
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutDependsOnTheEnginesOrderIsKeptUntouched() {
        var plan = Plan(
            [C("db", volumes: ["pgdata"]), C("api", volumes: ["uploads"]), C("web", volumes: ["static"])],
            ["pgdata", "static", "uploads"]);

        Assert.Equal(["db", "api", "web"], Names(plan.Stop));
        Assert.Equal(["web", "api", "db"], Names(plan.RestartOrder));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ADependsOnChainStopsDependentsFirstAndRestartsDependenciesFirst() {
        // Shuffled input: the graph, not the engine's listing, decides.
        var plan = Plan(
            [C("api", volumes: ["uploads"], dependsOn: ["db"]),
             C("web", volumes: ["static"], dependsOn: ["api"]),
             C("db", volumes: ["pgdata"])],
            ["pgdata", "static", "uploads"]);

        Assert.Equal(["web", "api", "db"], Names(plan.Stop));
        Assert.Equal(["db", "api", "web"], Names(plan.RestartOrder));
    }

    [Fact]
    public void DiamondDependenciesAreOrderedTransitively() {
        var plan = Plan(
            [C("web", volumes: ["static"], dependsOn: ["api", "cache"]),
             C("api", volumes: ["uploads"], dependsOn: ["db"]),
             C("cache", volumes: ["redis-data"], dependsOn: ["db"]),
             C("db", volumes: ["pgdata"])],
            ["pgdata", "redis-data", "static", "uploads"]);

        Assert.Equal(["web", "cache", "api", "db"], Names(plan.Stop));
        Assert.Equal(["db", "api", "cache", "web"], Names(plan.RestartOrder));
    }

    [Fact]
    public void ADependencyOnAServiceThatIsNotBeingStoppedImposesNoOrder() {
        // db is excluded, so it never goes down — api's edge to it constrains nothing, and api ends up
        // last in the stop order rather than being pushed ahead of a db that stays up.
        var plan = Plan(
            [C("api", volumes: ["uploads"], dependsOn: ["db"]),
             C("web", volumes: ["static"], dependsOn: ["api"]),
             C("db", volumes: ["pgdata"], exclude: "true")],
            ["pgdata", "static", "uploads"]);

        Assert.Equal(["web", "api"], Names(plan.Stop));
        Assert.Equal(BackupKeepReason.Excluded, Assert.Single(plan.Keep).Reason);
    }

    [Fact]
    public void ACircularDependsOnFallsBackToTheEnginesOrderWithAWarning() {
        var plan = Plan(
            [C("a", volumes: ["a-data"], dependsOn: ["b"]),
             C("b", volumes: ["b-data"], dependsOn: ["a"]),
             C("c", volumes: ["c-data"], dependsOn: ["a"])],
            ["a-data", "b-data", "c-data"]);

        Assert.Equal(["a", "b", "c"], Names(plan.Stop));
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("circular depends_on between services a, b, c", warning);
        Assert.Contains("falling back to the engine's container order", warning);
    }

    [Fact]
    public void AServiceDependingOnItselfIsNotACycle() {
        var plan = Plan(
            [C("api", volumes: ["uploads"], dependsOn: ["api", "db"]), C("db", volumes: ["pgdata"])],
            ["pgdata", "uploads"]);

        Assert.Equal(["api", "db"], Names(plan.Stop));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ReplicasOfOneServiceGoDownHighestNumberFirstAndComeBackLowestFirst() {
        var plan = Plan(
            [C("api-1", service: "api", volumes: ["uploads"], dependsOn: ["db"], number: 1),
             C("api-3", service: "api", volumes: ["uploads"], dependsOn: ["db"], number: 3),
             C("api-2", service: "api", volumes: ["uploads"], dependsOn: ["db"], number: 2),
             C("db", volumes: ["pgdata"])],
            ["pgdata", "uploads"]);

        Assert.Equal(["api-3", "api-2", "api-1", "db"], Names(plan.Stop));
        Assert.Equal(["db", "api-1", "api-2", "api-3"], Names(plan.RestartOrder));
    }

    [Fact]
    public void AContainerWithoutAComposeServiceStopsFirstAndRestartsLast() {
        var plan = Plan(
            [C("api", volumes: ["uploads"], dependsOn: ["db"]),
             Orphan("sidecar", ["scratch"]),
             C("db", volumes: ["pgdata"])],
            ["pgdata", "scratch", "uploads"]);

        Assert.Equal(["sidecar", "api", "db"], Names(plan.Stop));
        Assert.Equal(["db", "api", "sidecar"], Names(plan.RestartOrder));
    }

    [Fact]
    public void TheRestartOrderIsExactlyTheReverseOfTheStopOrder() {
        var plan = Plan(
            [C("web", volumes: ["static"], dependsOn: ["api"]),
             C("api", volumes: ["uploads"], dependsOn: ["db"]),
             C("db", volumes: ["pgdata"])],
            ["pgdata", "static", "uploads"]);

        Assert.Equal(Names(plan.Stop).Reverse(), Names(plan.RestartOrder));
    }

    [Fact]
    public void ThePlanIsTheSameWhateverOrderTheEngineListedTheContainersIn() {
        BackupContainer[] containers = [
            C("web", volumes: ["static"], dependsOn: ["api", "cache"]),
            C("api", volumes: ["uploads"], dependsOn: ["db"]),
            C("cache", volumes: ["redis-data"], dependsOn: ["db"]),
            C("db", volumes: ["pgdata"], exclude: "maybe"),
        ];
        var forward = Plan(containers, ["pgdata", "redis-data", "static", "uploads"]);
        var reversed = Plan([.. containers.Reverse()], ["uploads", "static", "redis-data", "pgdata"]);

        Assert.Equal(forward.Volumes, reversed.Volumes);
        Assert.Equal(Names(forward.Stop), Names(reversed.Stop));
        Assert.Equal(forward.Warnings, reversed.Warnings);
    }

    // ── Projection from the engine's container list ──────────────────────────

    [Fact]
    public void FromDockerReadsTheMountsLabelsAndStateItNeeds() {
        var container = BackupContainer.FromDocker(new DockerContainerInfo {
            Id = "0123456789abcdef0123",
            Names = ["/web-app-api-2"],
            Image = "api:latest",
            State = "Running",
            Status = "Up 3 hours",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) {
                [BackupPlan.ComposeServiceLabel] = "api",
                [BackupPlan.ComposeContainerNumberLabel] = "2",
                [BackupPlan.ComposeDependsOnLabel] = "db:service_healthy:true",
                [BackupPlan.ExcludeLabel] = "false",
                [BackupPlan.StopLabel] = "true",
                [BackupPlan.DumpLabel] = "postgres",
            },
            Mounts = [
                new DockerMountInfo { Type = "volume", Name = "web-app_uploads", Destination = "/uploads" },
                new DockerMountInfo { Type = "bind", Name = "", Destination = "/etc/localtime" },
                new DockerMountInfo { Type = "volume", Name = "", Destination = "/anon" },
                new DockerMountInfo { Type = "volume", Name = "web-app_uploads", Destination = "/also-uploads" },
            ],
        });

        Assert.Equal("web-app-api-2", container.DisplayName);
        Assert.Equal("api", container.Service);
        Assert.Equal(2, container.ContainerNumber);
        Assert.True(container.IsRunning);
        // Binds and anonymous volumes are not project volumes; a twice-mounted one is named once.
        Assert.Equal(["web-app_uploads"], container.VolumeNames);
        Assert.Equal(["db"], container.DependsOn);
        Assert.Equal("false", container.ExcludeLabel);
        Assert.Equal("true", container.StopLabel);
        // Carried verbatim — the dump policy, not the planner, decides what it means.
        Assert.Equal("postgres", container.DumpLabel);
    }

    [Fact]
    public void FromDockerToleratesAContainerWithNoLabelsNoMountsAndNoName() {
        var container = BackupContainer.FromDocker(new DockerContainerInfo {
            Id = "0123456789abcdef0123",
            Names = null!,
            Image = "api:latest",
            State = "exited",
            Status = "Exited (0) 1 hour ago",
            Labels = null!,
            Mounts = null!,
        });

        Assert.Equal("0123456789ab", container.DisplayName);
        Assert.Null(container.Service);
        Assert.Equal(1, container.ContainerNumber);
        Assert.False(container.IsRunning);
        Assert.Empty(container.VolumeNames);
        Assert.Empty(container.DependsOn);
        Assert.Null(container.ExcludeLabel);
    }

    [Fact]
    public void FromDockerFallsBackToTheWholeIdWhenItIsShorterThanAShortId() {
        var container = BackupContainer.FromDocker(new DockerContainerInfo {
            Id = "abc", Names = [], Image = "i", State = "running", Status = "Up",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
        });

        Assert.Equal("abc", container.DisplayName);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("db", new[] { "db" })]
    [InlineData("db:service_healthy:true", new[] { "db" })]
    [InlineData("db:service_healthy:true,cache:service_started:false", new[] { "db", "cache" })]
    [InlineData(" db:service_healthy:true , cache ", new[] { "db", "cache" })]
    [InlineData("db:service_healthy:true,db:service_started:false", new[] { "db" })]
    [InlineData(",,:broken,db", new[] { "db" })]
    public void ParseDependsOnReadsComposesLabel(string? label, string[] expected) =>
        Assert.Equal(expected, BackupContainer.ParseDependsOn(label));
}
