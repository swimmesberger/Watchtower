using System.Text.Json;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The archive's manifest. Two things are pinned here: an archive without dumps is byte-identical to
/// what Watchtower wrote before dumps existed — <c>formatVersion</c> describes the archive's shape,
/// not the version of the tool that wrote it — and a dump entry carries everything a restore needs to
/// match it back to a service without opening the SQL.
/// </summary>
public sealed class BackupManifestTests {
    private static readonly DateTimeOffset TakenAt = new(2026, 8, 21, 4, 5, 6, TimeSpan.Zero);

    private static Stack TestStack() => new() {
        Id = 7,
        Name = "web-app",
        RepositoryUrl = "https://example.invalid/web-app.git",
        ComposeFilePath = "docker-compose.yml",
        Branch = "main",
        ComposeProjectName = "web-app",
    };

    private static BackupService.BackupDumpEntry Dump(
        string service = "db", string file = "_dumps/db.sql", string[]? volumes = null) =>
        new(service, DumpEngine.Postgres, file, "postgres:16-alpine", "app", "web-app-db-1",
            volumes ?? ["web-app_pgdata"], ["app", "postgres"], 4096);

    [Fact]
    public void AStackWithoutADatabaseWritesTheSameManifestAsBefore() {
        var json = BackupService.BuildManifest(
            "prod", TestStack(), ["web-app_pgdata", "web-app_uploads"], TakenAt, encrypted: false, []);

        // Spelled out rather than reconstructed: this is the format an operator's own tooling — and
        // the manual-restore instructions in docs/backups.md — already read.
        Assert.Equal(
            """
            {"formatVersion":1,"tool":"watchtower","instance":"prod","stackId":7,"stackName":"web-app","composeProject":"web-app","volumes":["web-app_pgdata","web-app_uploads"],"createdAtUtc":"2026-08-21T04:05:06.0000000Z","encrypted":false}
            """,
            json);
    }

    [Fact]
    public void DumpsRaiseTheFormatVersionAndAreAppendedAfterTheV1Fields() {
        var json = BackupService.BuildManifest(
            "prod", TestStack(), ["web-app_uploads"], TakenAt, encrypted: true, [Dump()]);

        using var manifest = JsonDocument.Parse(json);
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("formatVersion").GetInt32());
        // The dumped database's volume is not in the archive — the dump is what stands in for it.
        Assert.Equal(
            new string?[] { "web-app_uploads" },
            root.GetProperty("volumes").EnumerateArray().Select(e => e.GetString()).ToArray());

        var dump = Assert.Single(root.GetProperty("dumps").EnumerateArray().ToArray());
        Assert.Equal("db", dump.GetProperty("service").GetString());
        Assert.Equal("postgres", dump.GetProperty("engine").GetString());
        Assert.Equal("_dumps/db.sql", dump.GetProperty("file").GetString());
        Assert.Equal("postgres:16-alpine", dump.GetProperty("image").GetString());
        Assert.Equal("app", dump.GetProperty("user").GetString());
        Assert.Equal("web-app-db-1", dump.GetProperty("container").GetString());
        Assert.Equal(
            new string?[] { "web-app_pgdata" },
            dump.GetProperty("volumes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new string?[] { "app", "postgres" },
            dump.GetProperty("databases").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(4096, dump.GetProperty("sizeBytes").GetInt64());
        // Appended, so a v1 reader that walks the object in order sees what it always saw first.
        Assert.EndsWith("\"dumps\":", json[..json.IndexOf("[{", StringComparison.Ordinal)]);
    }

    [Fact]
    public void ADumpWithoutADataVolumeRecordsAnEmptyVolumeList() {
        // A bind-mounted data directory: nothing was taken out of the archive for this dump.
        var json = BackupService.BuildManifest(
            "prod", TestStack(), ["web-app_uploads"], TakenAt, encrypted: false, [Dump(volumes: [])]);

        using var manifest = JsonDocument.Parse(json);
        var dump = Assert.Single(manifest.RootElement.GetProperty("dumps").EnumerateArray().ToArray());
        Assert.Empty(dump.GetProperty("volumes").EnumerateArray());
    }

    [Fact]
    public void EveryDumpGetsItsOwnEntry() {
        var json = BackupService.BuildManifest(
            "prod", TestStack(), [], TakenAt, encrypted: false,
            [Dump(), Dump("reporting", "_dumps/reporting.sql", ["web-app_reporting"])]);

        using var manifest = JsonDocument.Parse(json);
        Assert.Equal(
            new string?[] { "db", "reporting" },
            manifest.RootElement.GetProperty("dumps").EnumerateArray()
                .Select(d => d.GetProperty("service").GetString()).ToArray());
        Assert.Empty(manifest.RootElement.GetProperty("volumes").EnumerateArray());
    }
}
