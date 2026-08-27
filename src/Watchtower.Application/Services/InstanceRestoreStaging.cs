using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>A bundle unpacked on disk, waiting for its restore to be confirmed.</summary>
/// <param name="Directory">Where its members were extracted.</param>
/// <param name="Manifest">Its manifest.</param>
/// <param name="Secrets">Its secrets file.</param>
/// <param name="UploadedAtUtc">When it arrived.</param>
public sealed record StagedRestore(
    string Directory, BundleManifest Manifest, BundleSecrets Secrets, DateTimeOffset UploadedAtUtc) {
    /// <summary>The host path of one of the bundle's members.</summary>
    public string PathOf(BundleArchive archive) =>
        Path.Combine(Directory, archive.Entry.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// Holds the one uploaded bundle awaiting a restore, and the marker file that outlives the process a
/// restore stops (ADR-0027 §5).
/// </summary>
/// <remarks>
/// The directory is fixed rather than random because the completion pass has to find the marker after a
/// restart with no memory of having written it. Watchtower's container is stopped and started by the
/// coordinator, never recreated, so its filesystem is still there.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="rootDirectory">
/// Where uploads and the marker live. Defaults to the container's temp directory, which is what a
/// deployment wants; a test overrides it so two of them cannot meet in one path.
/// </param>
public sealed class InstanceRestoreStaging(
    ILogger<InstanceRestoreStaging> logger, string? rootDirectory = null) {
    /// <summary>The default root: one directory inside Watchtower's own container.</summary>
    public static string DefaultRootDirectory => Path.Combine(Path.GetTempPath(), "watchtower-restore");

    /// <summary>Where an uploaded bundle is unpacked and the progress marker is kept.</summary>
    public string RootDirectory { get; } = rootDirectory ?? DefaultRootDirectory;

    /// <summary>The marker file naming an in-flight restore.</summary>
    private string ProgressPath => Path.Combine(RootDirectory, "restore-progress.json");

    /// <summary>Where the SQL is placed inside the <em>database</em> container for the replay.</summary>
    public const string RemoteSqlPath = "/tmp/watchtower-restore/restore.sql";

    private readonly Lock _gate = new();
    private StagedRestore? _staged;

    /// <summary>The uploaded bundle, or null when there is none (or its directory has gone).</summary>
    public StagedRestore? Current {
        get {
            lock (_gate) {
                if (_staged is { } staged && !Directory.Exists(staged.Directory)) _staged = null;
                return _staged;
            }
        }
    }

    /// <summary>Publishes a newly unpacked bundle, discarding whatever it replaces.</summary>
    public void Replace(StagedRestore staged) {
        StagedRestore? previous;
        lock (_gate) {
            previous = _staged;
            _staged = staged;
        }
        if (previous is not null) DeleteDirectory(previous.Directory);
    }

    /// <summary>Discards the staged bundle and its files.</summary>
    public void Clear() {
        StagedRestore? previous;
        lock (_gate) {
            previous = _staged;
            _staged = null;
        }
        if (previous is not null) DeleteDirectory(previous.Directory);
    }

    /// <summary>A fresh directory for one upload, under <see cref="RootDirectory"/>.</summary>
    public string NewUploadDirectory() {
        var path = Path.Combine(RootDirectory, $"bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes the in-flight marker. Read back by the process that comes up after the restart.</summary>
    public async Task WriteProgressAsync(RestoreProgress progress, CancellationToken ct) {
        Directory.CreateDirectory(RootDirectory);
        await File.WriteAllTextAsync(
            ProgressPath, JsonSerializer.Serialize(progress, BackupBundle.JsonOptions), ct);
    }

    /// <summary>The in-flight marker, or null when no restore was under way.</summary>
    public RestoreProgress? ReadProgress() {
        try {
            if (!File.Exists(ProgressPath)) return null;
            return JsonSerializer.Deserialize<RestoreProgress>(
                File.ReadAllText(ProgressPath), BackupBundle.JsonOptions);
        } catch (Exception ex) {
            // A marker we cannot read is a marker we cannot act on. Reported, not thrown: the instance
            // is up, and refusing to finish starting over a stale temp file would be the worse outcome.
            logger.LogWarning(ex, "Could not read the restore progress marker at {Path}", ProgressPath);
            return null;
        }
    }

    /// <summary>Removes the in-flight marker, once its outcome has been recorded.</summary>
    public void ClearProgress() {
        try {
            if (File.Exists(ProgressPath)) File.Delete(ProgressPath);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Could not delete the restore progress marker at {Path}", ProgressPath);
        }
    }

    /// <summary>
    /// Unpacks a bundle tar into a fresh directory, refusing any entry that would escape it.
    /// </summary>
    /// <remarks>
    /// The entry names come from a file an operator uploaded, so <c>../</c> and absolute paths are
    /// exactly what has to be refused — a tar that writes outside its directory is writing wherever the
    /// Watchtower process can write.
    /// </remarks>
    /// <param name="tar">The uploaded tar.</param>
    /// <param name="directory">The (already created) directory to unpack into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The SHA-256 of every extracted member, by entry name.</returns>
    public static async Task<Dictionary<string, string>> ExtractAsync(
        Stream tar, string directory, CancellationToken ct) {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;

        await using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry) {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;

            // Only a leading "./" is stripped — some tar writers emit it. Everything else is left as it
            // is and put to the containment check below, so a name that tries to escape is *refused*
            // rather than quietly rewritten into a benign one.
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            var destination = Path.GetFullPath(
                Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The bundle contains an entry that would be written outside it ('{entry.Name}'). "
                    + "It was not produced by Watchtower, or it has been tampered with.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (entry.DataStream is not { } data) continue;
            await using (var file = File.Create(destination))
                await data.CopyToAsync(file, ct);

            await using var written = File.OpenRead(destination);
            digests[name] = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, ct));
        }
        return digests;
    }

    /// <summary>Deletes everything under <see cref="RootDirectory"/>, marker included.</summary>
    public void DeleteAll() => DeleteDirectory(RootDirectory);

    private void DeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Could not delete the restore staging directory {Directory}", path);
        }
    }
}
