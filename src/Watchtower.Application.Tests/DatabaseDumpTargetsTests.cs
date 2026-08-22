using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Which of a project's containers a run dumps instead of snapshotting (ADR-0017): the label
/// precedence, the demotions that fall back to a file snapshot, and which volume the dump replaces.
/// Every decision is made here, before anything is stopped, so this is where the contract lives — a
/// database that cannot be dumped must go down the old path rather than be skipped silently.
/// </summary>
public sealed class DatabaseDumpTargetsTests {

    // ── Builders ─────────────────────────────────────────────────────────────

    private static DockerContainerInfo Container(
        string service,
        string image = "postgres:16-alpine",
        string state = "running",
        (string Name, string Destination)[]? volumes = null,
        (string Source, string Destination)[]? binds = null,
        string? dump = null,
        string? exclude = null,
        string? name = null) {
        var labels = new Dictionary<string, string> { [BackupPlan.ComposeServiceLabel] = service };
        if (dump is not null) labels[BackupPlan.DumpLabel] = dump;
        if (exclude is not null) labels[BackupPlan.ExcludeLabel] = exclude;
        return new DockerContainerInfo {
            Id = $"{service}-id",
            Names = [$"/{name ?? service}"],
            Image = image,
            State = state,
            Status = state,
            Labels = labels,
            Mounts = [
                .. (volumes ?? []).Select(v => new DockerMountInfo {
                    Type = "volume", Name = v.Name, Destination = v.Destination, RW = true,
                }),
                .. (binds ?? []).Select(b => new DockerMountInfo {
                    Type = "bind", Source = b.Source, Destination = b.Destination, RW = true,
                }),
            ],
        };
    }

    private static (IReadOnlyList<DumpTarget> Targets, List<string> Log) Select(
        IReadOnlyList<DockerContainerInfo> containers,
        IReadOnlyDictionary<string, string?>? pgData = null) {
        var log = new List<string>();
        var targets = DatabaseDumpTargets.Select(
            containers, pgData ?? new Dictionary<string, string?>(), log.Add);
        return (targets, log);
    }

    private static readonly (string, string)[] DefaultData = [("app_pgdata", "/var/lib/postgresql/data")];

    // ── Detection ────────────────────────────────────────────────────────────

    [Fact]
    public void PicksThePostgresServiceAndLeavesTheRestOfTheStackAlone() {
        var (targets, log) = Select([
            Container("web", "nginx:alpine"),
            Container("api", "ghcr.io/org/api:1.2", volumes: [("app_uploads", "/data")]),
            Container("db", volumes: DefaultData),
        ]);

        var target = Assert.Single(targets);
        Assert.Equal("db", target.Service);
        Assert.Equal("db-id", target.ContainerId);
        Assert.Equal("db", target.ContainerName);
        Assert.Equal(DumpEngine.Postgres, target.Engine);
        Assert.Equal("app_pgdata", target.DataVolume);
        Assert.Equal(["app_pgdata"], target.MountedVolumes);
        Assert.Contains(log, l => l.Contains("Service 'db' is postgres") && l.Contains("app_pgdata is excluded"));
    }

    [Fact]
    public void OnlyTheCandidatesAreWorthAnInspectCall() {
        // The caller resolves PGDATA for these and nothing else — one round-trip per database, not
        // one per service in the project.
        var candidates = DatabaseDumpTargets.Candidates([
            Container("web", "nginx:alpine"),
            Container("db", volumes: DefaultData),
            Container("cache", "redis:7"),
            Container("old-db", state: "exited"),
        ]);

        Assert.Equal(["db-id"], candidates.Select(c => c.Id));
    }

    [Fact]
    public void TargetsAreOrderedByService() {
        var (targets, _) = Select([
            Container("zebra"), Container("alpha"), Container("middle"),
        ]);

        Assert.Equal(["alpha", "middle", "zebra"], targets.Select(t => t.Service));
    }

    [Fact]
    public void AContainerWithoutAComposeServiceIsNamedAfterItself() {
        var loose = Container("db", volumes: DefaultData, name: "lonely-postgres");
        loose = loose with { Labels = new Dictionary<string, string>() };

        var (targets, _) = Select([loose]);

        Assert.Equal("lonely-postgres", Assert.Single(targets).Service);
    }

    // ── Label precedence ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("FALSE")]
    public void AnOptOutSendsTheServiceBackToTheVolumeSnapshot(string value) {
        var (targets, log) = Select([Container("db", volumes: DefaultData, dump: value)]);

        Assert.Empty(targets);
        Assert.Contains(log, l => l.Contains("opted out of dumps") && l.Contains("snapshotting its volume(s)"));
        Assert.DoesNotContain(log, l => l.StartsWith("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExplicitEngineDumpsAnImageTheListDoesNotKnow() {
        var (targets, log) = Select([
            Container("db", "ghcr.io/acme/our-own-postgres-build:3", volumes: DefaultData, dump: "postgres"),
        ]);

        Assert.Equal("db", Assert.Single(targets).Service);
        Assert.DoesNotContain(log, l => l.StartsWith("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExcludedServiceIsNeverDumped() {
        // Exclusion is the operator saying the service is outside the run entirely; it takes its
        // volumes out of the archive, so there is nothing for a dump to stand in for.
        var (targets, log) = Select([
            Container("db", volumes: DefaultData, exclude: "true", dump: "postgres"),
        ]);

        Assert.Empty(targets);
        Assert.Empty(log);
    }

    [Fact]
    public void OptingInWithoutARecognizedImageWarnsAndFallsBackToTheSnapshot() {
        var (targets, log) = Select([
            Container("db", "edoburu/pgbouncer", volumes: DefaultData, dump: "true"),
        ]);

        Assert.Empty(targets);
        var warning = Assert.Single(log);
        Assert.StartsWith("WARNING: Service 'db' is labelled watchtower.backup.dump=true", warning);
        Assert.Contains("watchtower.backup.dump=postgres", warning);
    }

    [Fact]
    public void OptingInOnAPostgresImageIsJustTheImageRule() {
        var (targets, log) = Select([Container("db", volumes: DefaultData, dump: "true")]);

        Assert.Single(targets);
        Assert.DoesNotContain(log, l => l.StartsWith("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnrecognizedValueIsReportedAndTheImageDecides() {
        var (targets, log) = Select([Container("db", volumes: DefaultData, dump: "mysql")]);

        // Guessing either way would be invisible to the operator, so the value is reported and the
        // image — the only thing that can be checked — has the final say.
        Assert.Single(targets);
        Assert.Contains(log, l => l.StartsWith("WARNING", StringComparison.Ordinal)
            && l.Contains("unrecognized watchtower.backup.dump value 'mysql'"));
    }

    [Fact]
    public void AnUnrecognizedValueOnANonDatabaseImageIsJustIgnored() {
        var (targets, log) = Select([Container("web", "nginx:alpine", dump: "maybe")]);

        Assert.Empty(targets);
        Assert.Contains(log, l => l.StartsWith("WARNING", StringComparison.Ordinal) && l.EndsWith("ignoring it.", StringComparison.Ordinal));
    }

    // ── Demotions ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("exited")]
    [InlineData("paused")]
    [InlineData("restarting")]
    public void AServerThatIsNotRunningCannotBeDumped(string state) {
        var (targets, log) = Select([Container("db", state: state, volumes: DefaultData)]);

        // No live server to dump — and with the database down, the file snapshot of its data
        // directory is consistent anyway.
        Assert.Empty(targets);
        var warning = Assert.Single(log);
        Assert.StartsWith("WARNING: Service 'db' would be dumped but its container is", warning);
        Assert.Contains(state, warning);
        Assert.EndsWith("snapshotting its volume(s) instead.", warning);
    }

    // ── The data volume the dump replaces ────────────────────────────────────

    [Fact]
    public void PgDataInASubdirectoryStillResolvesToTheVolumeMountedAboveIt() {
        var db = Container("db", volumes: [("app_pgdata", "/var/lib/postgresql/data")]);
        var (targets, _) = Select([db],
            new Dictionary<string, string?> { ["db-id"] = "/var/lib/postgresql/data/pgdata" });

        Assert.Equal("app_pgdata", Assert.Single(targets).DataVolume);
    }

    [Fact]
    public void APgDataElsewhereEntirelyPicksTheVolumeMountedThere() {
        var db = Container("db", volumes: [("app_elsewhere", "/srv/pg"), ("app_pgdata", "/var/lib/postgresql/data")]);
        var (targets, _) = Select([db], new Dictionary<string, string?> { ["db-id"] = "/srv/pg" });

        Assert.Equal("app_elsewhere", Assert.Single(targets).DataVolume);
    }

    [Fact]
    public void TheBitnamiDataRootIsRecognizedWithoutPgData() {
        var (targets, _) = Select([
            Container("db", "bitnami/postgresql:16", volumes: [("app_pgdata", "/bitnami/postgresql")]),
        ]);

        Assert.Equal("app_pgdata", Assert.Single(targets).DataVolume);
    }

    [Fact]
    public void ABindMountedDataDirectoryLeavesNothingToExclude() {
        var (targets, log) = Select([
            Container("db", binds: [("/srv/pgdata", "/var/lib/postgresql/data")]),
        ]);

        var target = Assert.Single(targets);
        // The dump still happens — it is the bind mount that is nobody's to exclude.
        Assert.Null(target.DataVolume);
        Assert.Empty(target.MountedVolumes);
        Assert.Contains(log, l => l.Contains("not a named volume"));
    }

    [Fact]
    public void AnAnonymousVolumeAtTheDataDirectoryIsNotAnExclusion() {
        var db = Container("db");
        db = db with {
            Mounts = [new DockerMountInfo { Type = "volume", Name = "", Destination = "/var/lib/postgresql/data" }],
        };

        Assert.Null(Assert.Single(Select([db]).Targets).DataVolume);
    }

    [Fact]
    public void TheServicesOtherVolumesStayInTheArchive() {
        var (targets, log) = Select([
            Container("db", volumes: [("app_pgdata", "/var/lib/postgresql/data"), ("app_dbconf", "/etc/pg")]),
        ]);

        var target = Assert.Single(targets);
        Assert.Equal("app_pgdata", target.DataVolume);
        Assert.Equal(["app_pgdata", "app_dbconf"], target.MountedVolumes);
        Assert.Contains(log, l => l.Contains("also mounts app_dbconf") && l.Contains("still snapshotted"));
    }
}
