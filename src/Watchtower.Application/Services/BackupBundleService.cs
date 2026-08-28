using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>A finished bundle waiting on disk for its download.</summary>
/// <param name="Path">Host path of the tar.</param>
/// <param name="FileName">The name it is offered under.</param>
/// <param name="SizeBytes">Its size.</param>
/// <param name="CreatedAtUtc">When the export finished.</param>
/// <param name="StackCount">How many stack archives it carries.</param>
/// <param name="MissingStackCount">How many stacks had no archive to carry.</param>
public sealed record StagedBundle(
    string Path, string FileName, long SizeBytes, DateTimeOffset CreatedAtUtc,
    int StackCount, int MissingStackCount);

/// <summary>
/// Holds the one bundle that is ready to download. Process-local and deliberately not persisted: the
/// tar lives in the container's own filesystem, so a restart loses both halves together and the export
/// is cheap to repeat.
/// </summary>
public sealed class BundleExportState {
    private readonly Lock _gate = new();
    private StagedBundle? _staged;

    /// <summary>The staged bundle, or null when there is none (or its file has since gone).</summary>
    public StagedBundle? Current {
        get {
            lock (_gate) {
                if (_staged is { } staged && !File.Exists(staged.Path)) _staged = null;
                return _staged;
            }
        }
    }

    /// <summary>Publishes a new bundle and deletes the one it replaces.</summary>
    public void Replace(StagedBundle staged) {
        StagedBundle? previous;
        lock (_gate) {
            previous = _staged;
            _staged = staged;
        }
        Delete(previous);
    }

    /// <summary>Drops and deletes the staged bundle, if any.</summary>
    public void Clear() {
        StagedBundle? previous;
        lock (_gate) {
            previous = _staged;
            _staged = null;
        }
        Delete(previous);
    }

    /// <summary>
    /// Removes a replaced bundle, directory and all. Each export stages into a directory of its own, so
    /// this can never reach the bundle that replaced it — two exports within the same second would
    /// otherwise agree on a file name, and deleting "the old one" would delete the new one's bytes.
    /// </summary>
    private static void Delete(StagedBundle? staged) {
        if (staged is null) return;
        try {
            var directory = Path.GetDirectoryName(staged.Path);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        } catch (IOException) {
            // A download may still be reading it. It is in the container's temp directory, so the
            // worst case is a file that outlives the process rather than one that is never freed.
        } catch (UnauthorizedAccessException) {
            // Same.
        }
    }
}

/// <summary>
/// Builds the exportable full backup bundle (ADR-0027 §4): a fresh dump of Watchtower's own database
/// plus the newest archive of every stack, in one plain tar with a manifest and the out-of-database
/// secrets, staged on disk for an admin to download.
/// </summary>
/// <remarks>
/// Runs as a job on the single-flight backup queue rather than inside the request that asked for it: it
/// takes a dump and downloads every stack's newest archive, which is minutes of work against the same
/// spool disk and storage connection a stack backup uses.
/// </remarks>
public sealed class BackupBundleService(
    InstanceBackupService instanceBackup,
    BackupStorageFactory storageFactory,
    BundleExportState state,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    AuditLog audit,
    ILogger<BackupBundleService> logger) {
    /// <summary>How the bundle export names itself in the audit trail and the run log.</summary>
    internal const string AuditTarget = "watchtower (bundle)";

    /// <summary>Where staged bundles are written, under the process temp directory.</summary>
    private static string StagingDirectory => Path.Combine(Path.GetTempPath(), "watchtower-bundle");

    /// <summary>
    /// Minimum age before a staged entry counts as abandoned by a dead process. Two hours is far
    /// beyond any live export-download window, and a leftover surviving until the next startup
    /// after a quick restart only costs temp-directory disk.
    /// </summary>
    internal static readonly TimeSpan StagingReclaimAge = TimeSpan.FromHours(2);

    /// <summary>
    /// Reclaims bundles left by a previous process. Called at startup: the state that knew about
    /// them did not survive, so nothing will ever hand them out — they would just occupy the disk.
    /// Only entries older than <see cref="StagingReclaimAge"/> are deleted: the staging root lives
    /// in the machine's <em>shared</em> temp directory, so another live Watchtower process — above
    /// all a parallel test host, which is how this used to flake in CI — may have a bundle staged
    /// there that its state still hands out, and age is the only thing distinguishing that from a
    /// dead process's leftovers.
    /// </summary>
    public void CleanStagingDirectory() {
        try {
            ReclaimStaleStagingEntries(StagingDirectory, StagingReclaimAge);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Could not clear the bundle staging directory {Directory}", StagingDirectory);
        }
    }

    /// <summary>
    /// Deletes every per-export directory under <paramref name="root"/> whose last write is older
    /// than <paramref name="minAge"/>. The directory's write time tracks the bundle being written
    /// into it, so it is the staging time.
    /// </summary>
    internal static void ReclaimStaleStagingEntries(string root, TimeSpan minAge) {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow - minAge;
        foreach (var entry in Directory.EnumerateDirectories(root)) {
            if (Directory.GetLastWriteTimeUtc(entry) < cutoff)
                Directory.Delete(entry, recursive: true);
        }
    }

    /// <summary>Builds the bundle for an event created by <see cref="BackupQueueService.EnqueueBundleExport"/>.</summary>
    /// <param name="backupEventId">The stackless event tracking this export.</param>
    /// <param name="ct">The worker's token.</param>
    public async Task ExecuteExportAsync(int backupEventId, CancellationToken ct) {
        var output = new StringBuilder();
        void Log(string line) { lock (output) output.AppendLine(line); }

        string triggeredBy;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var evt = await db.BackupEvents.FirstOrDefaultAsync(e => e.Id == backupEventId, ct);
            if (evt is null) return;
            triggeredBy = evt.TriggeredBy;
            evt.Status = BackupStatuses.Running;
            evt.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var backup = options.CurrentValue.Backup;
        try {
            var staged = await BuildAsync(backup, Log, ct);
            state.Replace(staged);
            await FinishAsync(backupEventId, success: true, output.ToString(), staged.FileName, staged.SizeBytes);
            await audit.RecordAsync(BackupService.AuditCategory, "bundle.export", AuditTarget,
                $"{triggeredBy} · {staged.StackCount} stack archive(s)"
                + (staged.MissingStackCount > 0 ? $" · {staged.MissingStackCount} stack(s) with no archive" : "")
                + $" · {staged.SizeBytes} bytes → {staged.FileName}",
                ct: CancellationToken.None);
            logger.LogInformation(
                "Backup bundle staged: {FileName} ({SizeBytes} bytes)", staged.FileName, staged.SizeBytes);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            Log($"FAILED: {ex.Message}");
            await FinishAsync(backupEventId, success: false, output.ToString(), remotePath: null, sizeBytes: null);
            await audit.RecordAsync(BackupService.AuditCategory, "bundle.export", AuditTarget,
                InstanceBackupService.Summary(triggeredBy, backup),
                success: false, error: ex.Message, ct: CancellationToken.None);
            logger.LogWarning(ex, "Backup bundle export failed");
        }
    }

    /// <summary>Takes the dump, collects the stack archives and writes the tar.</summary>
    private async Task<StagedBundle> BuildAsync(
        BackupOptions backup, Action<string> log, CancellationToken ct) {
        // The instance archive is taken fresh rather than read back off the storage: a bundle is a
        // point-in-time copy of *this* instance, and the newest stored dump could be a day old.
        log("Backing up Watchtower's own database for the bundle…");
        var instance = await instanceBackup.RunAsync(backup, log, ct);

        var createdAt = DateTimeOffset.UtcNow;
        var fileName = BackupBundle.FileName(backup.ResolveInstanceName(), createdAt);
        // A directory per export, so the path is unique even when two exports agree on the file name —
        // the name is second-resolution, and it is what the operator downloads, not what identifies it.
        var stagingDirectory = Path.Combine(StagingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var bundlePath = Path.Combine(stagingDirectory, fileName);
        var spools = new List<string>();

        try {
            using var storage = storageFactory.Create(backup);

            // Downloaded to disk first, because a tar entry has to declare its length before its bytes.
            var instanceSpool = await SpoolAsync(storage, instance.RelativePath, spools, ct);
            var instanceEntry =
                $"{BackupBundle.InstanceDirectory}/{instance.FileName}";
            var instanceArchive = new BundleArchive(
                instanceEntry, instance.RelativePath, new FileInfo(instanceSpool).Length,
                await Sha256Async(instanceSpool, ct), instance.TakenAt, Encrypted: true);

            var stacks = new List<(BundleStack Stack, string? Spool)>();
            foreach (var stack in await LoadStacksAsync(ct)) {
                var directory = BackupNaming.ResolveDirectory(stack, backup.ResolveInstanceName());
                var newest = await NewestArchiveAsync(storage, directory, ct);
                if (newest is not { } found) {
                    log($"WARNING: stack '{stack.Name}' has no archive on the storage — the bundle "
                        + "carries its definition but not its data.");
                    stacks.Add((
                        new BundleStack(stack.Id, stack.Name, stack.ComposeProjectName, null,
                            "no archive on the backup storage"),
                        null));
                    continue;
                }

                var storagePath = $"{directory}/{found.File.Name}";
                var spool = await SpoolAsync(storage, storagePath, spools, ct);
                log($"Collected '{stack.Name}': {found.File.Name} ({found.File.SizeBytes} bytes)");
                stacks.Add((
                    new BundleStack(stack.Id, stack.Name, stack.ComposeProjectName,
                        new BundleArchive(
                            $"{BackupBundle.StacksDirectory}/{storagePath}", storagePath,
                            new FileInfo(spool).Length, await Sha256Async(spool, ct), found.TakenAt,
                            found.File.Name.EndsWith(".enc", StringComparison.Ordinal)),
                        Reason: null),
                    spool));
            }

            var manifest = new BundleManifest(
                BackupBundle.FormatVersion, "watchtower", createdAt, backup.ResolveInstanceName(),
                InstanceVersion.App, await LastMigrationAsync(ct),
                KeyProtectionSecretConfigured: !string.IsNullOrEmpty(options.CurrentValue.Auth.KeyProtectionSecret),
                instanceArchive, [.. stacks.Select(s => s.Stack)]);

            await using (var tar = File.Create(bundlePath))
            await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true)) {
                await WriteJsonAsync(writer, BackupBundle.ManifestEntry, manifest, ct);
                // 0600 on the secrets, matching how the dumps are written into the helper container:
                // the mode is not protection on its own, but a file this sensitive should not be
                // world-readable the moment someone untars it as root.
                await WriteJsonAsync(
                    writer, BackupBundle.SecretsEntry, BuildSecrets(options.CurrentValue), ct,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await WriteFileAsync(writer, instanceEntry, instanceSpool, ct);
                foreach (var (stack, spool) in stacks)
                    if (stack.Archive is { } archive && spool is not null)
                        await WriteFileAsync(writer, archive.Entry, spool, ct);
            }

            var sizeBytes = new FileInfo(bundlePath).Length;
            var carried = stacks.Count(s => s.Stack.Archive is not null);
            log($"Bundle complete: {sizeBytes} bytes, {carried} stack archive(s).");
            return new StagedBundle(
                bundlePath, fileName, sizeBytes, createdAt, carried, stacks.Count - carried);
        } catch {
            try {
                Directory.Delete(stagingDirectory, recursive: true);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Could not delete the partial bundle {BundlePath}", bundlePath);
            }
            throw;
        } finally {
            foreach (var spool in spools) {
                try {
                    if (File.Exists(spool)) File.Delete(spool);
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to delete bundle spool file {SpoolPath}", spool);
                }
            }
        }
    }

    /// <summary>The stacks a bundle describes, in a stable order so two exports read the same.</summary>
    private async Task<IReadOnlyList<Stack>> LoadStacksAsync(CancellationToken ct) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Stacks.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
    }

    private async Task<string?> LastMigrationAsync(CancellationToken ct) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await InstanceVersion.LastMigrationAsync(db, ct);
    }

    /// <summary>The newest Watchtower-named archive in one directory, or null when there is none.</summary>
    private static async Task<(BackupStorageFile File, DateTimeOffset TakenAt)?> NewestArchiveAsync(
        IBackupStorage storage, string directory, CancellationToken ct) =>
        (await storage.ListFilesAsync(directory, ct))
        .Select(f => (File: f, TakenAt: BackupNaming.ParseTimestamp(f.Name)))
        .Where(x => x.TakenAt is not null)
        .OrderByDescending(x => x.TakenAt)
        .Select(x => ((BackupStorageFile, DateTimeOffset)?)(x.File, x.TakenAt!.Value))
        .FirstOrDefault();

    /// <summary>Downloads one archive to a temp file and registers it for cleanup.</summary>
    private static async Task<string> SpoolAsync(
        IBackupStorage storage, string relativePath, List<string> spools, CancellationToken ct) {
        var spool = Path.Combine(Path.GetTempPath(), $"watchtower-bundle-{Guid.NewGuid():N}.spool");
        spools.Add(spool);
        await using var file = File.Create(spool);
        await storage.DownloadAsync(relativePath, file, ct);
        return spool;
    }

    /// <summary>
    /// The out-of-database material, exactly as this instance holds it (ADR-0027 §4). Plain text: see
    /// <see cref="BundleSecrets"/> for why that is the decision rather than an oversight.
    /// </summary>
    internal static BundleSecrets BuildSecrets(WatchtowerOptions options) {
        var backup = options.Backup;
        return new BundleSecrets(
            SecretsFormatVersion: 1,
            KeyProtectionSecret: options.Auth.KeyProtectionSecret,
            BackupEncryptionPassphrase: backup.EncryptionPassphrase,
            BackupInstanceName: backup.ResolveInstanceName(),
            Storage: new BundleStorageSecrets(
                Provider: backup.ResolveProvider() == BackupProviderKind.Local ? "local" : "sftp",
                Sftp: new BundleSftpSecrets(
                    backup.Sftp.Host, backup.Sftp.Port, backup.Sftp.Username, backup.Sftp.Password,
                    backup.Sftp.PrivateKey, backup.Sftp.PrivateKeyPassphrase, backup.Sftp.BasePath),
                LocalBasePath: backup.Local.BasePath));
    }

    private static async Task WriteJsonAsync<T>(
        TarWriter writer, string entryName, T value, CancellationToken ct,
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
            | UnixFileMode.OtherRead) {
        using var content = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(value, BackupBundle.JsonOptions));
        await writer.WriteEntryAsync(
            new PaxTarEntry(TarEntryType.RegularFile, entryName) { Mode = mode, DataStream = content }, ct);
    }

    private static async Task WriteFileAsync(
        TarWriter writer, string entryName, string sourcePath, CancellationToken ct) {
        await using var content = File.OpenRead(sourcePath);
        await writer.WriteEntryAsync(
            new PaxTarEntry(TarEntryType.RegularFile, entryName) {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                DataStream = content,
            }, ct);
    }

    /// <summary>Lowercase hex SHA-256 of a file, so an import can prove the tar arrived intact.</summary>
    private static async Task<string> Sha256Async(string path, CancellationToken ct) {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    private async Task FinishAsync(
        int backupEventId, bool success, string output, string? remotePath, long? sizeBytes) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var evt = await db.BackupEvents.FirstOrDefaultAsync(e => e.Id == backupEventId, CancellationToken.None);
        if (evt is null) return;
        evt.Status = success ? BackupStatuses.Success : BackupStatuses.Failed;
        evt.Output = output.Replace("\0", "");
        // Not a storage path: the bundle never leaves this host. The file name is what the UI shows.
        evt.RemotePath = remotePath;
        evt.SizeBytes = sizeBytes;
        evt.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
