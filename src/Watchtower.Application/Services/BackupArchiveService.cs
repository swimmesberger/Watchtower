using System.Formats.Tar;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

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
    public async Task WriteArchiveAsync(
        IReadOnlyList<string> volumeNames, string? manifestJson, Stream destination,
        string helperImage, CancellationToken ct) {
        if (volumeNames.Count == 0)
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
            if (manifestJson is not null)
                await docker.PutContainerArchiveAsync(
                    containerId, MountRoot, BuildManifestTar(manifestJson), ct);

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

    private async Task EnsureHelperImageAsync(string image, CancellationToken ct) {
        if (await docker.ImageExistsAsync(image, ct)) return;
        logger.LogInformation("Pulling backup helper image {Image}", image);
        await docker.PullImageAsync(image, ct: ct);
    }

    /// <summary>A one-entry tar containing the manifest, for the archive PUT endpoint.</summary>
    private static MemoryStream BuildManifestTar(string manifestJson) {
        var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, leaveOpen: true)) {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "backup-manifest.json") {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)),
            };
            writer.WriteEntry(entry);
        }
        buffer.Position = 0;
        return buffer;
    }
}
