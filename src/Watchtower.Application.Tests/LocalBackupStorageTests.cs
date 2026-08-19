using System.Text;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The local-directory storage provider (ADR-0016 §3) — also the reference behaviour the SFTP
/// provider mirrors: parent directories created on upload, partial files never left behind as
/// finished backups, and no relative path allowed to escape the base directory.
/// </summary>
public sealed class LocalBackupStorageTests : IDisposable {
    private readonly string _root = Directory.CreateTempSubdirectory("wt-backup-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LocalBackupStorage NewStorage() => new(_root);

    [Fact]
    public async Task UploadCreatesDirectoriesAndWritesTheContent() {
        var storage = NewStorage();
        await storage.UploadAsync("nas/web-app/backup.tar.gz", async (stream, ct) => {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("archive-bytes"), ct);
        }, CancellationToken.None);

        var path = Path.Combine(_root, "nas", "web-app", "backup.tar.gz");
        Assert.Equal("archive-bytes", File.ReadAllText(path));
        // The partial spool name must be gone once the upload finished.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial"));
    }

    [Fact]
    public async Task AFailedUploadLeavesNoFileBehind() {
        var storage = NewStorage();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UploadAsync("nas/web-app/backup.tar.gz",
                (_, _) => throw new InvalidOperationException("writer died"), CancellationToken.None));

        // Neither the final name nor a partial may exist — a torn upload must not look finished.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "nas", "web-app")));
    }

    [Fact]
    public async Task ListReturnsFilesWithSizesAndToleratesAMissingDirectory() {
        var storage = NewStorage();
        Assert.Empty(await storage.ListFilesAsync("nas/none", CancellationToken.None));

        await storage.UploadAsync("nas/app/a.tar.gz", Write("a"), CancellationToken.None);
        await storage.UploadAsync("nas/app/bb.tar.gz", Write("bb"), CancellationToken.None);

        var files = (await storage.ListFilesAsync("nas/app", CancellationToken.None))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        Assert.Equal([new("a.tar.gz", 1), new BackupStorageFile("bb.tar.gz", 2)], files);
    }

    [Fact]
    public async Task DownloadStreamsTheUploadedContentBack() {
        var storage = NewStorage();
        await storage.UploadAsync("nas/app/a.tar.gz", Write("round-trip"), CancellationToken.None);

        var output = new MemoryStream();
        await storage.DownloadAsync("nas/app/a.tar.gz", output, CancellationToken.None);
        Assert.Equal("round-trip", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task DeleteRemovesExactlyTheNamedFile() {
        var storage = NewStorage();
        await storage.UploadAsync("nas/app/a.tar.gz", Write("a"), CancellationToken.None);
        await storage.UploadAsync("nas/app/b.tar.gz", Write("b"), CancellationToken.None);

        await storage.DeleteFileAsync("nas/app/a.tar.gz", CancellationToken.None);

        var files = await storage.ListFilesAsync("nas/app", CancellationToken.None);
        Assert.Equal("b.tar.gz", Assert.Single(files).Name);
    }

    [Fact]
    public async Task APathEscapingTheBaseDirectoryIsRejected() {
        var storage = NewStorage();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UploadAsync("../outside.tar.gz", Write("x"), CancellationToken.None));
    }

    private static Func<Stream, CancellationToken, Task> Write(string content) =>
        async (stream, ct) => await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
}
