using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Matching an archive's dumps against the stack that is about to be restored (ADR-0017 §5). This is
/// the last point at which a restore can still be refused for free: everything after it stops
/// containers and erases volumes, so a dump whose file is missing, or whose service is gone, has to
/// be caught here rather than discovered halfway through.
/// </summary>
public sealed class RestoreDumpPlanTests {

    // ── Builders ─────────────────────────────────────────────────────────────

    private static DockerContainerInfo Container(
        string service, string image = "postgres:16-alpine", string state = "running",
        string? name = null, string? dump = null) {
        var labels = new Dictionary<string, string> { [BackupPlan.ComposeServiceLabel] = service };
        if (dump is not null) labels[BackupPlan.DumpLabel] = dump;
        return new DockerContainerInfo {
            Id = $"{name ?? service}-id",
            Names = [$"/{name ?? service}"],
            Image = image,
            State = state,
            Status = state,
            Labels = labels,
        };
    }

    private static string Entry(
        string service = "db", string file = "_dumps/db.sql", string engine = "postgres",
        string user = "app", string databases = "\"app\",\"postgres\"", string volumes = "\"web-app_pgdata\"") =>
        $$"""
        {"service":"{{service}}","engine":"{{engine}}","file":"{{file}}","image":"postgres:16-alpine",
        "user":"{{user}}","container":"web-app-db-1","volumes":[{{volumes}}],
        "databases":[{{databases}}],"sizeBytes":4096}
        """;

    private static string Manifest(string entries, int formatVersion = 2) =>
        $$"""
        {"formatVersion":{{formatVersion}},"tool":"watchtower","stackName":"web-app",
        "volumes":["web-app_uploads"],"dumps":[{{entries}}]}
        """;

    private static BackupArchiveContents Archive(string? manifest, params string[] dumpFiles) =>
        new(["web-app_uploads"], manifest) { DumpFiles = dumpFiles };

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public void AFileWithItsManifestEntryBecomesAReplay() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry()), "_dumps/db.sql"), [Container("web", "nginx:alpine"), Container("db")]);

        var replay = Assert.Single(plan.Replays);
        Assert.Equal("_dumps/db.sql", replay.File);
        Assert.Equal("db", replay.Service);
        Assert.Equal(DumpEngine.Postgres, replay.Engine);
        Assert.Equal("app", replay.User);
        Assert.Equal(["app", "postgres"], replay.ExpectedDatabases);
        Assert.Equal("db-id", replay.ContainerId);
        Assert.Equal("db", replay.ContainerName);
        Assert.Empty(plan.Errors);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void TheVolumesTheDumpsReplaceAreReportedWithTheirService() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry()), "_dumps/db.sql"), [Container("db")]);

        // The restore says "left in place" for these instead of warning that the archive is missing
        // a volume the host has.
        Assert.Equal(["web-app_pgdata"], plan.DumpCoveredVolumes);
        Assert.Equal("db", plan.CoveredBy["web-app_pgdata"]);
    }

    [Fact]
    public void ReplaysAreOrderedByService() {
        var manifest = Manifest(
            $"{Entry("reporting", "_dumps/reporting.sql", volumes: "\"web-app_reporting\"")},{Entry()}");
        var plan = RestoreDumpPlan.Match(
            Archive(manifest, "_dumps/reporting.sql", "_dumps/db.sql"),
            [Container("reporting"), Container("db")]);

        Assert.Equal(["db", "reporting"], plan.Replays.Select(r => r.Service));
        Assert.Equal(["web-app_pgdata", "web-app_reporting"], plan.DumpCoveredVolumes);
    }

    [Fact]
    public void AV1ArchiveHasNothingToReplay() {
        var plan = RestoreDumpPlan.Match(
            Archive("""{"formatVersion":1,"volumes":["web-app_uploads"]}"""), [Container("db")]);

        Assert.Empty(plan.Replays);
        Assert.Empty(plan.Errors);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.DumpCoveredVolumes);
    }

    // ── The table of contents is the truth, the manifest is metadata ─────────

    [Fact]
    public void AFileTheManifestDoesNotMentionIsStillReplayed_ByItsFileName() {
        var plan = RestoreDumpPlan.Match(Archive(Manifest(""), "_dumps/db.sql"), [Container("db")]);

        var replay = Assert.Single(plan.Replays);
        Assert.Equal("db", replay.Service);
        Assert.Null(replay.User);
        Assert.Empty(replay.ExpectedDatabases);
        Assert.Contains(plan.Warnings, w => w.Contains("which the manifest does not describe"));
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void AnArchiveWithoutAManifestReplaysWhatItPhysicallyContains() {
        var plan = RestoreDumpPlan.Match(Archive(null, "_dumps/db.sql"), [Container("db")]);

        Assert.Equal("db", Assert.Single(plan.Replays).Service);
        Assert.Single(plan.Warnings);
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void AManifestThatCannotBeParsedIsReportedAndTheFilesAreUsed() {
        var plan = RestoreDumpPlan.Match(Archive("{not json at all", "_dumps/db.sql"), [Container("db")]);

        // Refusing a restore over unreadable metadata would be the wrong call — the SQL is right there.
        Assert.Single(plan.Replays);
        Assert.Contains(plan.Warnings, w => w.Contains("manifest could not be read"));
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void AManifestEntryWithoutItsFileRefusesTheRestore() {
        var plan = RestoreDumpPlan.Match(Archive(Manifest(Entry())), [Container("db")]);

        // Proceeding would wipe the volumes and never put the database back.
        Assert.Empty(plan.Replays);
        var error = Assert.Single(plan.Errors);
        Assert.Contains("the manifest lists a dump of service 'db' at 'backup/_dumps/db.sql'", error);
        Assert.Contains("does not contain that file", error);
    }

    // ── The stack has to be able to take the dump ────────────────────────────

    [Fact]
    public void ADumpForAServiceThisStackDoesNotHaveRefusesTheRestore() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry()), "_dumps/db.sql"), [Container("web", "nginx:alpine")]);

        Assert.Empty(plan.Replays);
        Assert.Contains("this stack has no container for that service", Assert.Single(plan.Errors));
    }

    [Fact]
    public void AServiceThatNoLongerRunsPostgresRefusesTheRestore() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry()), "_dumps/db.sql"), [Container("db", "mysql:8")]);

        Assert.Empty(plan.Replays);
        var error = Assert.Single(plan.Errors);
        Assert.Contains("now runs 'mysql:8', which is not a Postgres image", error);
        Assert.Contains("watchtower.backup.dump=postgres", error);
    }

    [Fact]
    public void ALabelledContainerCountsAsPostgresEvenWithAnUnknownImage() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry()), "_dumps/db.sql"),
            [Container("db", "ghcr.io/acme/our-own-postgres-build:3", dump: "postgres")]);

        Assert.Single(plan.Replays);
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void AnEngineThisVersionCannotReplayRefusesTheRestore() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry(engine: "mysql", file: "_dumps/db.sql")), "_dumps/db.sql"),
            [Container("db")]);

        Assert.Empty(plan.Replays);
        Assert.Contains("cannot replay", Assert.Single(plan.Errors));
    }

    [Fact]
    public void AFileThatIsNotSqlAndHasNoEntryIsNotGuessedAt() {
        var plan = RestoreDumpPlan.Match(Archive(Manifest(""), "_dumps/db.dump"), [Container("db")]);

        Assert.Empty(plan.Replays);
        Assert.Single(plan.Errors);
    }

    [Fact]
    public void AStoppedContainerIsStillAValidTarget() {
        // The restore starts it and waits for it; being down is not a reason to refuse.
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry()), "_dumps/db.sql"), [Container("db", state: "exited")]);

        Assert.Equal("db-id", Assert.Single(plan.Replays).ContainerId);
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void AScaledServiceReplaysIntoTheFirstContainerByName_WithAWarning() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry()), "_dumps/db.sql"),
            [Container("db", name: "web-app-db-2"), Container("db", name: "web-app-db-1")]);

        // They share one server, so the replay only has to happen once — but the operator should know
        // which one it went into.
        Assert.Equal("web-app-db-1", Assert.Single(plan.Replays).ContainerName);
        Assert.Contains(plan.Warnings, w => w.Contains("has 2 containers"));
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void ANewerArchiveFormatIsReplayedWithAForwardCompatibilityWarning() {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry(), formatVersion: 4), "_dumps/db.sql"), [Container("db")]);

        Assert.Single(plan.Replays);
        Assert.Contains(plan.Warnings, w => w.Contains("formatVersion 4") && w.Contains("newer than this Watchtower"));
        Assert.Empty(plan.Errors);
    }

    /// <summary>
    /// <b>The reader must never be behind its own writer.</b> Restoring an archive this build wrote is
    /// the single most common restore there is, and it must be silent about the format — a
    /// "newer than this Watchtower understands" line over Watchtower's own output is an operator
    /// stopping a restore over nothing. The version is read from <see cref="BackupService"/> rather
    /// than spelled, so the next manifest bump cannot reintroduce the drift without failing here.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    // When ManifestFormatVersion next bumps, add the previous version as a literal InlineData row —
    // archives already in the wild must keep their no-warning coverage (reviewer note, stage 7).
    [InlineData(BackupService.ManifestFormatVersion)]
    public void AnArchiveThisBuildCouldHaveWritten_ProducesNoFormatWarning(int formatVersion) {
        var plan = RestoreDumpPlan.Match(
            Archive(Manifest(Entry(), formatVersion: formatVersion), "_dumps/db.sql"), [Container("db")]);

        Assert.Single(plan.Replays);
        Assert.Empty(plan.Errors);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("formatVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce() {
        var manifest = Manifest(
            $"{Entry()},{Entry("reporting", "_dumps/reporting.sql", volumes: "\"web-app_reporting\"")}");
        var plan = RestoreDumpPlan.Match(
            Archive(manifest, "_dumps/db.sql", "_dumps/orphan.sql"),
            [Container("db", "mysql:8")]);

        // A missing file, a service that is not Postgres any more, and a file whose service does not
        // exist — one refusal, all three reasons.
        Assert.Equal(3, plan.Errors.Count);
        Assert.Empty(plan.Replays);
    }
}
