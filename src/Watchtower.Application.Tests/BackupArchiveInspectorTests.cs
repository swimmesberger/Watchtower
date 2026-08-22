using System.Formats.Tar;
using System.Text;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The restore flow's table-of-contents scan (ADR-0016): volume names are the first-level
/// directories under <c>backup/</c>, the manifest rides next to them, and anything else — stray
/// files, entries outside <c>backup/</c> — is ignored rather than mistaken for a volume.
/// </summary>
public sealed class BackupArchiveInspectorTests {
    [Fact]
    public async Task FindsVolumesAndTheManifest() {
        var tar = BuildTar(writer => {
            AddDir(writer, "backup/");
            AddFile(writer, "backup/backup-manifest.json", """{"stackName":"web-app"}""");
            AddDir(writer, "backup/web-app_pgdata/");
            AddFile(writer, "backup/web-app_pgdata/base/1234", "pg bytes");
            AddDir(writer, "backup/web-app_uploads/");
        });

        var contents = await BackupArchiveInspector.InspectAsync(tar, CancellationToken.None);

        Assert.Equal(["web-app_pgdata", "web-app_uploads"], contents.Volumes);
        Assert.Contains("web-app", contents.ManifestJson);
    }

    [Fact]
    public async Task AVolumeIsFoundEvenWithoutItsDirectoryEntry() {
        // Some tar producers omit intermediate directory entries — the file path alone must count.
        var tar = BuildTar(writer => AddFile(writer, "backup/data/file.txt", "x"));
        var contents = await BackupArchiveInspector.InspectAsync(tar, CancellationToken.None);
        Assert.Equal(["data"], contents.Volumes);
        Assert.Null(contents.ManifestJson);
    }

    [Fact]
    public async Task StrayFilesAndForeignRootsAreIgnored() {
        var tar = BuildTar(writer => {
            AddFile(writer, "backup/README.txt", "not a volume");
            AddFile(writer, "elsewhere/data/file.txt", "outside backup/");
        });
        var contents = await BackupArchiveInspector.InspectAsync(tar, CancellationToken.None);
        Assert.Empty(contents.Volumes);
    }

    [Fact]
    public async Task TheDumpDirectoryIsNotAVolume_AndItsFilesAreListedAsDumps() {
        var tar = BuildTar(writer => {
            AddDir(writer, "backup/");
            AddFile(writer, "backup/backup-manifest.json", """{"formatVersion":2}""");
            AddDir(writer, "backup/_dumps/");
            AddFile(writer, "backup/_dumps/db.sql", "DROP DATABASE app;");
            AddFile(writer, "backup/_dumps/reporting.sql", "DROP DATABASE reports;");
            AddDir(writer, "backup/web-app_uploads/");
        });

        var contents = await BackupArchiveInspector.InspectAsync(tar, CancellationToken.None);

        // Restoring "_dumps" as if it were a volume is exactly what an older Watchtower does with a
        // v2 archive; this reader knows the name is reserved.
        Assert.Equal(["web-app_uploads"], contents.Volumes);
        Assert.Equal(["_dumps/db.sql", "_dumps/reporting.sql"], contents.DumpFiles);
    }

    [Fact]
    public async Task ADumpDirectoryWithoutFilesYieldsNeitherAVolumeNorADump() {
        var tar = BuildTar(writer => AddDir(writer, "backup/_dumps/"));
        var contents = await BackupArchiveInspector.InspectAsync(tar, CancellationToken.None);
        Assert.Empty(contents.Volumes);
        Assert.Empty(contents.DumpFiles);
    }

    [Fact]
    public async Task AV1ArchiveSimplyHasNoDumps() {
        var tar = BuildTar(writer => {
            AddFile(writer, "backup/backup-manifest.json", """{"formatVersion":1}""");
            AddFile(writer, "backup/web-app_pgdata/base/1234", "pg bytes");
        });

        var contents = await BackupArchiveInspector.InspectAsync(tar, CancellationToken.None);

        Assert.Equal(["web-app_pgdata"], contents.Volumes);
        Assert.Empty(contents.DumpFiles);
    }

    private static MemoryStream BuildTar(Action<TarWriter> write) {
        var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, leaveOpen: true))
            write(writer);
        buffer.Position = 0;
        return buffer;
    }

    private static void AddDir(TarWriter writer, string name) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));

    private static void AddFile(TarWriter writer, string name, string content) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        });
}
