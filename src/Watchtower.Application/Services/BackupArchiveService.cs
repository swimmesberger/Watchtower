using System.Formats.Tar;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// A host-side file to place inside the archive next to the volume directories — a database dump.
/// </summary>
/// <param name="RelativePath">
/// Path inside the archive, relative to its <c>backup/</c> root (e.g. <c>_dumps/db.sql</c>).
/// </param>
/// <param name="SourcePath">The host file to read the content from.</param>
public sealed record BackupExtraFile(string RelativePath, string SourcePath);

/// <summary>
/// Turns a set of named volumes into one tar stream, without mounting anything into Watchtower's own
/// container (ADR-0016 §1): a helper container is <em>created but never started</em> with each volume
/// bind-mounted read-only under <c>/backup/{volume}</c>, the daemon's archive endpoint streams the
/// tar out, and the helper is removed again. No code executes in the helper, so any pullable image
/// works. Also powers the UI's single-volume download.
/// </summary>
public sealed class BackupArchiveService(DockerEngineClient docker, ILogger<BackupArchiveService> logger) {
    /// <summary>Label stamped on helper containers, so a leaked one is identifiable (and cleanable).</summary>
    internal const string HelperLabel = "dev.watchtower.backup-helper";

    /// <summary>Container-side directory the volumes are mounted under; the tar's top-level entry.</summary>
    internal const string MountRoot = "/backup";

    /// <summary>
    /// Streams a tar of the given volumes (entries rooted <c>backup/…</c>) into
    /// <paramref name="destination"/> — typically a gzip/encryption pipeline. When
    /// <paramref name="manifestJson"/> is set it is injected as <c>backup/backup-manifest.json</c>
    /// so the archive self-describes.
    /// </summary>
    public Task WriteArchiveAsync(
        IReadOnlyList<string> volumeNames, string? manifestJson, Stream destination,
        string helperImage, CancellationToken ct) =>
        WriteArchiveAsync(volumeNames, manifestJson, [], destination, helperImage, ct);

    /// <summary>
    /// Same, plus host-side files placed under the archive's <c>backup/</c> root — the database dumps
    /// that stand in for the volumes they replaced (ADR-0017).
    /// </summary>
    /// <remarks>
    /// The manifest and the extras go in with a single push-stream PUT at <c>/</c>, streamed straight
    /// from disk into the request body, so a dump of any size costs no memory and no second copy. The
    /// tar carries an explicit <c>backup/</c> directory entry with a real mode: it is what creates the
    /// directory when <em>no</em> volume is mounted (a stack whose only state is a dumped database),
    /// and leaving the mode at zero would chmod the mount root out of the daemon's own reach before it
    /// reads the archive back.
    /// </remarks>
    /// <param name="volumeNames">The named volumes to archive; may be empty when there are extras.</param>
    /// <param name="manifestJson">The manifest, or null for a bare single-volume download.</param>
    /// <param name="extraFiles">Host files to add under <c>backup/</c>.</param>
    /// <param name="destination">Where the tar goes — typically a gzip/encryption pipeline.</param>
    /// <param name="helperImage">Image for the never-started helper container.</param>
    /// <param name="ct">The run's token.</param>
    public async Task WriteArchiveAsync(
        IReadOnlyList<string> volumeNames, string? manifestJson, IReadOnlyList<BackupExtraFile> extraFiles,
        Stream destination, string helperImage, CancellationToken ct) {
        if (volumeNames.Count == 0 && extraFiles.Count == 0)
            throw new InvalidOperationException("No volumes to archive.");

        await EnsureHelperImageAsync(helperImage, ct);

        var containerId = await docker.CreateContainerAsync(new DockerCreateContainerBody {
            Image = helperImage,
            // Never started, so the command is irrelevant — set explicitly anyway so creation also
            // works from images that declare no default command.
            Cmd = ["true"],
            Labels = new Dictionary<string, string> { [HelperLabel] = "1" },
            HostConfig = new DockerCreateHostConfig {
                Binds = [.. volumeNames.Select(v => $"{v}:{MountRoot}/{v}:ro")],
                NetworkMode = "none",
            },
        }, name: $"watchtower-backup-{Guid.NewGuid():N}"[..32], ct);

        try {
            if (manifestJson is not null || extraFiles.Count > 0)
                await docker.PutContainerArchiveAsync(
                    containerId, "/",
                    (stream, token) => WriteInjectedTarAsync(stream, manifestJson, extraFiles, token), ct);

            await using var archive = await docker.GetContainerArchiveAsync(containerId, MountRoot, ct);
            await archive.CopyToAsync(destination, ct);
        } finally {
            try {
                await docker.RemoveContainerAsync(containerId, CancellationToken.None);
            } catch (Exception ex) {
                // The helper is labelled, so a leak is findable; don't mask the primary failure.
                logger.LogWarning(ex, "Failed to remove backup helper container {ContainerId}", containerId);
            }
        }
    }

    /// <summary>
    /// Extracts a backup tar back into the given volumes — the exact inverse of
    /// <see cref="WriteArchiveAsync"/>: a helper container mounts each volume <b>read-write</b>
    /// under <c>/backup/{volume}</c>, is started once to clear the volumes' current contents (the
    /// only step that executes code in the helper, so the image must carry a shell — the default
    /// busybox does), then the daemon's archive-extract endpoint unpacks the tar's
    /// <c>backup/{volume}/…</c> entries straight into the mounts, preserving ownership and modes.
    /// </summary>
    /// <param name="tarStream">The <b>uncompressed</b> tar (decrypt/gunzip upstream).</param>
    public async Task RestoreArchiveAsync(
        IReadOnlyList<string> volumeNames, Stream tarStream, string helperImage, CancellationToken ct) {
        if (volumeNames.Count == 0)
            throw new InvalidOperationException("No volumes to restore into.");

        await EnsureHelperImageAsync(helperImage, ct);

        var containerId = await docker.CreateContainerAsync(new DockerCreateContainerBody {
            Image = helperImage,
            // Clears every mounted volume (only the target volumes are mounted). The dot-globs pick
            // up hidden entries; unmatched globs pass through literally and rm -f ignores them.
            Cmd = ["sh", "-c", "rm -rf /backup/*/* /backup/*/.[!.]* /backup/*/..?*"],
            Labels = new Dictionary<string, string> { [HelperLabel] = "1" },
            HostConfig = new DockerCreateHostConfig {
                Binds = [.. volumeNames.Select(v => $"{v}:{MountRoot}/{v}")],
                NetworkMode = "none",
            },
        }, name: $"watchtower-restore-{Guid.NewGuid():N}"[..32], ct);

        try {
            await docker.StartContainerAsync(containerId, ct);
            var exitCode = await docker.WaitContainerAsync(containerId, ct);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Clearing the volumes failed (helper exit code {exitCode}). " +
                    "The helper image must provide `sh` and `rm` — the default busybox does.");

            await docker.PutContainerArchiveAsync(containerId, "/",
                (destination, token) => CopyVolumeEntriesAsync(tarStream, volumeNames, destination, token), ct);
        } finally {
            try {
                await docker.RemoveContainerAsync(containerId, CancellationToken.None);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to remove restore helper container {ContainerId}", containerId);
            }
        }
    }

    /// <summary>
    /// Copies only the target volumes' entries (rooted <c>backup/{volume}/…</c>, plus the
    /// <c>backup/</c> root itself) from the archive tar into the request body. The archive can carry
    /// far more than what is being restored — above all a database dump standing in for a
    /// multi-gigabyte volume that is deliberately left in place — and pushing those entries too
    /// would stream the whole archive through the daemon and write it into the helper's layer for
    /// nothing. That is not only wasted time: it is what used to push a small restore past the
    /// HTTP client's ceiling.
    /// </summary>
    private static async Task CopyVolumeEntriesAsync(
        Stream tar, IReadOnlyList<string> volumeNames, Stream destination, CancellationToken ct) {
        var root = MountRoot.TrimStart('/');
        var prefixes = volumeNames.Select(v => $"{root}/{v}/").ToArray();
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        await using var reader = new TarReader(tar, leaveOpen: true);
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry) {
            var name = entry.Name.TrimStart('.', '/');
            var wanted = name == root || name == $"{root}/"
                || prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)
                    || name == p[..^1]);
            if (!wanted) continue;
            await writer.WriteEntryAsync(entry, ct);
        }
    }

    private async Task EnsureHelperImageAsync(string image, CancellationToken ct) {
        if (await docker.ImageExistsAsync(image, ct)) return;
        logger.LogInformation("Pulling backup helper image {Image}", image);
        await docker.PullImageAsync(image, ct: ct);
    }

    /// <summary>
    /// Writes the injected tar — <c>backup/</c>, the manifest and the extra files — straight into the
    /// PUT request's body. The extras are streamed from disk one at a time, so the largest thing ever
    /// held in memory is the manifest.
    /// </summary>
    private static async Task WriteInjectedTarAsync(
        Stream destination, string? manifestJson, IReadOnlyList<BackupExtraFile> extraFiles,
        CancellationToken ct) {
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var directory in Directories(extraFiles))
            await writer.WriteEntryAsync(
                new PaxTarEntry(TarEntryType.Directory, directory) { Mode = DirectoryMode }, ct);

        if (manifestJson is not null) {
            using var manifest = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
            await writer.WriteEntryAsync(
                new PaxTarEntry(TarEntryType.RegularFile, $"{ArchiveRoot}backup-manifest.json") {
                    Mode = RegularFileMode,
                    DataStream = manifest,
                }, ct);
        }

        foreach (var file in extraFiles) {
            await using var content = File.OpenRead(file.SourcePath);
            await writer.WriteEntryAsync(
                new PaxTarEntry(TarEntryType.RegularFile, ArchiveRoot + file.RelativePath.TrimStart('/')) {
                    Mode = RegularFileMode,
                    DataStream = content,
                }, ct);
        }
    }

    /// <summary>
    /// The directory entries the injected tar needs: the archive root, plus every directory the extra
    /// files sit in. Written explicitly rather than left to the daemon's implicit parent creation, so
    /// the modes are ours and an archive with no volumes still has a root to be read back from.
    /// </summary>
    private static IReadOnlyList<string> Directories(IReadOnlyList<BackupExtraFile> extraFiles) {
        var directories = new SortedSet<string>(StringComparer.Ordinal) { ArchiveRoot };
        foreach (var file in extraFiles) {
            var segments = file.RelativePath.TrimStart('/').Split('/');
            var prefix = ArchiveRoot;
            for (var i = 0; i < segments.Length - 1; i++) {
                prefix += segments[i] + "/";
                directories.Add(prefix);
            }
        }
        return [.. directories];
    }

    /// <summary>The tar path prefix every entry of a backup archive carries.</summary>
    private const string ArchiveRoot = "backup/";

    /// <summary>0755 — the daemon has to be able to walk into the directories it just created.</summary>
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>0644 — a manifest and a dump are read, never executed.</summary>
    private const UnixFileMode RegularFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
}
