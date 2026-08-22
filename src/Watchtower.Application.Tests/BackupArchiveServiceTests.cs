using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// How one archive is assembled against a faked daemon (ADR-0016 §1, ADR-0017): a helper container
/// that mounts the volumes read-only and is never started, and a single push-stream PUT that injects
/// the manifest and the database dumps next to them. The PUT is the part with a real failure mode —
/// it lands at <c>/</c> and carries the <c>backup/</c> directory itself, which is what lets a stack
/// whose only state is a dumped database produce an archive at all.
/// </summary>
public sealed class BackupArchiveServiceTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string HelperImage = "busybox:stable";

    private static DockerClientEstate Estate() =>
        DockerClientEstate.Create(pruneTimeout: TimeSpan.FromMinutes(30));

    private static BackupArchiveService Service(DockerClientEstate estate) =>
        new(estate.Client, NullLogger<BackupArchiveService>.Instance);

    /// <summary>The one request that carries the injected tar, with its body.</summary>
    private static (string Request, byte[] Body) InjectedPut(DockerClientEstate estate) {
        var index = estate.Default.Requests.FindIndex(r => r.Contains("/archive?path=", StringComparison.Ordinal));
        Assert.True(index >= 0, "no archive PUT was sent");
        Assert.Single(estate.Default.Requests, r => r.Contains("/archive?path=", StringComparison.Ordinal));
        return (estate.Default.Requests[index], estate.Default.BodyBytes[index]!);
    }

    /// <summary>Entry name → content (null for directories), in the order they were written.</summary>
    private static async Task<List<(string Name, string? Content)>> ReadTarAsync(byte[] tar) {
        var entries = new List<(string, string?)>();
        await using var reader = new TarReader(new MemoryStream(tar));
        while (await reader.GetNextEntryAsync(cancellationToken: TestContext.Current.CancellationToken) is { } entry) {
            if (entry.DataStream is not { } data) {
                entries.Add((entry.Name, null));
                continue;
            }
            using var buffer = new MemoryStream();
            await data.CopyToAsync(buffer, TestContext.Current.CancellationToken);
            entries.Add((entry.Name, Encoding.UTF8.GetString(buffer.ToArray())));
        }
        return entries;
    }

    private static string WriteDump(string content) {
        var path = Path.Combine(Path.GetTempPath(), $"watchtower-dump-test-{Guid.NewGuid():N}.sql");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task TheHelperMountsEveryVolumeReadOnlyAndIsRemovedAgain() {
        using var estate = Estate();
        using var destination = new MemoryStream();

        await Service(estate).WriteArchiveAsync(
            ["app_pgdata", "app_uploads"], """{"formatVersion":1}""", destination, HelperImage, Ct);

        var create = estate.Default.Requests.FindIndex(r => r.Contains("/containers/create", StringComparison.Ordinal));
        Assert.Contains("name=watchtower-backup-", estate.Default.Requests[create]);
        using var body = JsonDocument.Parse(estate.Default.Bodies[create]!);
        var root = body.RootElement;
        Assert.Equal(HelperImage, root.GetProperty("Image").GetString());
        Assert.Equal(
            new string?[] { "app_pgdata:/backup/app_pgdata:ro", "app_uploads:/backup/app_uploads:ro" },
            root.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("none", root.GetProperty("HostConfig").GetProperty("NetworkMode").GetString());
        Assert.Equal("1", root.GetProperty("Labels").GetProperty(BackupArchiveService.HelperLabel).GetString());
        // The volumes are read out on the long-running client — an archive of a large volume outlives
        // the default 100-second ceiling.
        Assert.Contains(estate.LongRunning.Requests, r => r.Contains("/archive?path=%2Fbackup", StringComparison.Ordinal));
        // A helper left behind would pin every volume it mounts.
        Assert.Contains(estate.Default.Requests,
            r => r.EndsWith($"/containers/{RecordingHandler.CreatedContainerId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheManifestAndTheDumpsGoInWithOneTarAtTheRoot() {
        using var estate = Estate();
        using var destination = new MemoryStream();
        var first = WriteDump("-- db dump\n");
        var second = WriteDump("-- reporting dump\n");

        try {
            await Service(estate).WriteArchiveAsync(
                ["app_uploads"], """{"formatVersion":2}""",
                [new BackupExtraFile("_dumps/db.sql", first), new BackupExtraFile("_dumps/reporting.sql", second)],
                destination, HelperImage, Ct);
        } finally {
            File.Delete(first);
            File.Delete(second);
        }

        var (request, tar) = InjectedPut(estate);
        // At "/" rather than "/backup": one request carries the directory and everything in it.
        Assert.Contains("/archive?path=%2F", request);
        Assert.DoesNotContain("path=%2Fbackup", request);
        var entries = await ReadTarAsync(tar);
        Assert.Equal(
            ["backup/", "backup/_dumps/", "backup/backup-manifest.json", "backup/_dumps/db.sql", "backup/_dumps/reporting.sql"],
            entries.Select(e => e.Name));
        Assert.Equal("""{"formatVersion":2}""", entries.Single(e => e.Name.EndsWith("manifest.json", StringComparison.Ordinal)).Content);
        Assert.Equal("-- db dump\n", entries.Single(e => e.Name.EndsWith("db.sql", StringComparison.Ordinal)).Content);
        Assert.Equal("-- reporting dump\n", entries.Single(e => e.Name.EndsWith("reporting.sql", StringComparison.Ordinal)).Content);
    }

    [Fact]
    public async Task TheDirectoriesAreWrittenWithAModeTheDaemonCanStillWalkInto() {
        using var estate = Estate();
        using var destination = new MemoryStream();
        var dump = WriteDump("-- db dump\n");

        try {
            await Service(estate).WriteArchiveAsync(
                [], null, [new BackupExtraFile("_dumps/db.sql", dump)], destination, HelperImage, Ct);
        } finally {
            File.Delete(dump);
        }

        // Mode 0 on backup/ would chmod the mount root out of reach between the PUT and the read-back.
        await using var reader = new TarReader(new MemoryStream(InjectedPut(estate).Body));
        var root = await reader.GetNextEntryAsync(cancellationToken: Ct);
        Assert.Equal(TarEntryType.Directory, root!.EntryType);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            root.Mode);
    }

    [Fact]
    public async Task AStackWhoseOnlyStateIsADumpNeedsNoVolumeAtAll() {
        using var estate = Estate();
        using var destination = new MemoryStream();
        var dump = WriteDump("-- db dump\n");

        try {
            await Service(estate).WriteArchiveAsync(
                [], """{"formatVersion":2}""", [new BackupExtraFile("_dumps/db.sql", dump)],
                destination, HelperImage, Ct);
        } finally {
            File.Delete(dump);
        }

        var create = estate.Default.Requests.FindIndex(r => r.Contains("/containers/create", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(estate.Default.Bodies[create]!);
        // The helper still has to be creatable with nothing bound into it.
        Assert.Empty(body.RootElement.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray());
        Assert.Contains("backup/_dumps/db.sql", (await ReadTarAsync(InjectedPut(estate).Body)).Select(e => e.Name));
    }

    [Fact]
    public async Task AnArchiveWithNeitherVolumesNorDumpsIsRefused() {
        using var estate = Estate();
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(estate).WriteArchiveAsync([], "{}", [], destination, HelperImage, Ct));

        Assert.Empty(estate.Default.Requests);
    }

    [Fact]
    public async Task ASingleVolumeDownloadStillSendsNoTarWhenItHasNoManifest() {
        using var estate = Estate();
        using var destination = new MemoryStream();

        await Service(estate).WriteArchiveAsync(["app_uploads"], null, destination, HelperImage, Ct);

        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/archive?path=%2F&", StringComparison.Ordinal));
        Assert.DoesNotContain(estate.Default.Requests, r => r.EndsWith("/archive?path=%2F", StringComparison.Ordinal));
    }
}
