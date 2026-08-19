using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Executes one stack backup end to end (ADR-0016): resolve the stack's compose volumes, optionally
/// stop its running containers for a consistent snapshot, spool the (gzipped, optionally encrypted)
/// volume archive to a temp file, restart the containers, upload, then apply retention. Progress and
/// the outcome are recorded on the run's <see cref="BackupEvent"/>.
/// </summary>
/// <remarks>
/// Singleton driven by <see cref="BackupQueueService"/>'s worker; reaches the scoped DbContext
/// through <see cref="IServiceScopeFactory"/> (ADR-0004). The archive is spooled locally
/// <em>before</em> the upload so the container-stop window covers only the snapshot, never the
/// (possibly slow) network transfer.
/// </remarks>
public sealed class BackupService(
    DockerEngineClient docker,
    BackupArchiveService archiveService,
    BackupStorageFactory storageFactory,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<BackupService> logger) {

    /// <summary>The compose label a stack's volumes carry.</summary>
    private const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>Runs the backup for an event created by <see cref="BackupQueueService.Enqueue"/>.</summary>
    public async Task ExecuteBackupAsync(int backupEventId, CancellationToken ct) {
        var output = new StringBuilder();
        void Log(string line) => output.AppendLine(line);

        int stackId;
        Stack? stack;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var evt = await db.BackupEvents.FirstOrDefaultAsync(e => e.Id == backupEventId, ct);
            if (evt is null) return; // stack (and its events) deleted while queued
            stackId = evt.StackId;
            stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stackId, ct);
            evt.Status = "running";
            evt.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (stack is null) {
            await FinishAsync(backupEventId, success: false, "Stack no longer exists.", null, null, ct);
            return;
        }

        try {
            var backup = options.CurrentValue.Backup;
            var result = await RunAsync(stack, backup, Log, ct);
            await FinishAsync(backupEventId, success: true, output.ToString(), result.RemotePath, result.SizeBytes, ct);
            logger.LogInformation(
                "Backup of stack {StackName} completed: {RemotePath} ({SizeBytes} bytes)",
                stack.Name, result.RemotePath, result.SizeBytes);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            Log($"FAILED: {ex.Message}");
            await FinishAsync(backupEventId, success: false, output.ToString(), null, null, ct);
            logger.LogWarning(ex, "Backup of stack {StackName} failed", stack.Name);
        }
    }

    /// <summary>
    /// Runs a restore enqueued by <see cref="BackupQueueService.TryEnqueueRestore"/>: download the
    /// archive, scan its table of contents, stop the stack's containers, wipe the target volumes
    /// and extract the archive back into them (ADR-0016), restart. Only volumes present in BOTH the
    /// archive and on the host are touched; mismatches are logged, never guessed at.
    /// </summary>
    public async Task ExecuteRestoreAsync(int backupEventId, string fileName, CancellationToken ct) {
        var output = new StringBuilder();
        void Log(string line) => output.AppendLine(line);

        Stack? stack;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var evt = await db.BackupEvents.FirstOrDefaultAsync(e => e.Id == backupEventId, ct);
            if (evt is null) return;
            stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == evt.StackId, ct);
            evt.Status = "running";
            evt.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (stack is null) {
            await FinishAsync(backupEventId, success: false, "Stack no longer exists.", null, null, ct);
            return;
        }

        try {
            var backup = options.CurrentValue.Backup;
            var result = await RunRestoreAsync(stack, fileName, backup, Log, ct);
            await FinishAsync(backupEventId, success: true, output.ToString(), result.RemotePath, result.SizeBytes, ct);
            logger.LogInformation("Restore of {RemotePath} into stack {StackName} completed", result.RemotePath, stack.Name);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            Log($"FAILED: {ex.Message}");
            await FinishAsync(backupEventId, success: false, output.ToString(), null, null, ct);
            logger.LogWarning(ex, "Restore into stack {StackName} failed", stack.Name);
        }
    }

    private async Task<RunResult> RunRestoreAsync(
        Stack stack, string fileName, BackupOptions backup, Action<string> log, CancellationToken ct) {
        var encrypted = fileName.EndsWith(".enc", StringComparison.Ordinal);
        if (encrypted && string.IsNullOrEmpty(backup.EncryptionPassphrase))
            throw new InvalidOperationException(
                "The archive is encrypted but no encryption passphrase is configured.");

        var directory = BackupNaming.StackDirectory(backup.ResolveInstanceName(), stack.Name);
        var relativePath = $"{directory}/{fileName}";

        var spoolPath = Path.Combine(Path.GetTempPath(), $"watchtower-restore-{Guid.NewGuid():N}.spool");
        try {
            // 1. Download the archive to a local spool (two read passes follow: scan, then extract).
            using (var storage = storageFactory.Create(backup)) {
                log($"Downloading {relativePath} from {storage.Description}");
                await using var spool = File.Create(spoolPath);
                await storage.DownloadAsync(relativePath, spool, ct);
            }
            var sizeBytes = new FileInfo(spoolPath).Length;
            log($"Downloaded {sizeBytes} bytes{(encrypted ? " (encrypted)" : "")}");

            // 2. Scan the table of contents and match it against the host's volumes.
            var contents = await WithSpoolTarAsync(spoolPath, encrypted, backup,
                tar => ReadContentsAsync(tar, encrypted, ct));
            if (contents.ManifestJson is null)
                log("Note: the archive carries no manifest (single-volume download?) — proceeding by its directory layout.");

            var hostVolumes = (await docker.ListVolumesAsync(ct))
                .Where(v => v.Labels is { } labels && labels.TryGetValue(ComposeProjectLabel, out var p)
                    && p == stack.ComposeProjectName)
                .Select(v => v.Name)
                .ToList();
            var targets = contents.Volumes.Intersect(hostVolumes, StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            foreach (var archiveOnly in contents.Volumes.Except(hostVolumes, StringComparer.Ordinal))
                log($"WARNING: archive volume '{archiveOnly}' does not exist on this host — skipped.");
            foreach (var hostOnly in hostVolumes.Except(contents.Volumes, StringComparer.Ordinal))
                log($"WARNING: host volume '{hostOnly}' is not in the archive — left untouched.");
            if (targets.Count == 0)
                throw new InvalidOperationException(
                    "None of the archive's volumes exist on this host. Deploy the stack first so its volumes exist.");
            log($"Restoring {targets.Count} volume(s): {string.Join(", ", targets)}");

            // 3. Stop, wipe + extract, restart. Always stopped for a restore — extracting under a
            // running application is never sound.
            var stopped = await StopRunningContainersAsync(stack.ComposeProjectName, log, ct);
            try {
                await WithSpoolTarAsync<object?>(spoolPath, encrypted, backup, async tar => {
                    await archiveService.RestoreArchiveAsync(targets, tar, backup.HelperImage, ct);
                    return null;
                });
                log("Archive extracted into the volumes.");
            } finally {
                await RestartContainersAsync(stopped, log);
            }

            return new RunResult(relativePath, sizeBytes);
        } finally {
            try {
                if (File.Exists(spoolPath)) File.Delete(spoolPath);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to delete restore spool file {SpoolPath}", spoolPath);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> over the spool opened as an uncompressed tar stream
    /// (file → optional decrypt → gunzip), disposing every layer afterwards — including the file
    /// handle, which the delete in the caller's finally depends on.
    /// </summary>
    private static async Task<T> WithSpoolTarAsync<T>(
        string spoolPath, bool encrypted, BackupOptions backup, Func<Stream, Task<T>> action) {
        await using var file = File.OpenRead(spoolPath);
        var inner = encrypted
            ? BackupEncryption.CreateDecryptingStream(file, backup.EncryptionPassphrase!)
            : file;
        try {
            await using var tar = new GZipStream(inner, CompressionMode.Decompress, leaveOpen: true);
            return await action(tar);
        } finally {
            if (!ReferenceEquals(inner, file)) await inner.DisposeAsync();
        }
    }

    /// <summary>Scans the tar, translating decode failures on encrypted archives into a passphrase hint.</summary>
    private static async Task<BackupArchiveContents> ReadContentsAsync(
        Stream tar, bool encrypted, CancellationToken ct) {
        try {
            return await BackupArchiveInspector.InspectAsync(tar, ct);
        } catch (Exception ex) when (encrypted
            && ex is InvalidDataException or System.Security.Cryptography.CryptographicException) {
            throw new InvalidOperationException(
                $"Could not read the encrypted archive — is the encryption passphrase the one it was written with? ({ex.Message})");
        }
    }

    private sealed record RunResult(string RemotePath, long SizeBytes);

    private async Task<RunResult> RunAsync(
        Stack stack, BackupOptions backup, Action<string> log, CancellationToken ct) {
        var project = stack.ComposeProjectName;
        var takenAt = DateTimeOffset.UtcNow;
        var encrypted = !string.IsNullOrEmpty(backup.EncryptionPassphrase);

        // 1. The stack's volumes, by compose project label.
        var volumes = (await docker.ListVolumesAsync(ct))
            .Where(v => v.Labels is { } labels && labels.TryGetValue(ComposeProjectLabel, out var p) && p == project)
            .Select(v => v.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (volumes.Count == 0)
            throw new InvalidOperationException(
                $"No volumes found for compose project '{project}'. Has the stack been deployed?");
        log($"Backing up {volumes.Count} volume(s) of project '{project}': {string.Join(", ", volumes)}");

        var instance = backup.ResolveInstanceName();
        var directory = BackupNaming.StackDirectory(instance, stack.Name);
        var fileName = BackupNaming.FileName(project, takenAt, encrypted);
        var relativePath = $"{directory}/{fileName}";
        var manifest = BuildManifest(instance, stack, volumes, takenAt, encrypted);

        // 2. Snapshot to a local spool file — stopping containers only for this step, not the upload.
        var spoolPath = Path.Combine(Path.GetTempPath(), $"watchtower-{fileName}.spool");
        try {
            var stopped = stack.BackupStopContainers
                ? await StopRunningContainersAsync(project, log, ct)
                : [];
            try {
                await using var spool = File.Create(spoolPath);
                Stream sink = spool;
                try {
                    if (encrypted)
                        sink = BackupEncryption.CreateEncryptingStream(spool, backup.EncryptionPassphrase!);
                    await using (var gzip = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true))
                        await archiveService.WriteArchiveAsync(volumes, manifest, gzip, backup.HelperImage, ct);
                } finally {
                    if (!ReferenceEquals(sink, spool)) await sink.DisposeAsync(); // flush final cipher block
                }
            } finally {
                await RestartContainersAsync(stopped, log);
            }

            var sizeBytes = new FileInfo(spoolPath).Length;
            log($"Snapshot complete: {sizeBytes} bytes{(encrypted ? " (encrypted)" : "")}");

            // 3. Upload + retention.
            using var storage = storageFactory.Create(backup);
            log($"Uploading to {storage.Description}: {relativePath}");
            await storage.UploadAsync(relativePath, async (dest, uploadCt) => {
                await using var read = File.OpenRead(spoolPath);
                await read.CopyToAsync(dest, uploadCt);
            }, ct);

            await ApplyRetentionAsync(storage, directory, backup, log, ct);
            return new RunResult(relativePath, sizeBytes);
        } finally {
            try {
                if (File.Exists(spoolPath)) File.Delete(spoolPath);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to delete backup spool file {SpoolPath}", spoolPath);
            }
        }
    }

    /// <summary>Stops the project's running containers; returns them in stop order for the restart.</summary>
    private async Task<IReadOnlyList<DockerContainerInfo>> StopRunningContainersAsync(
        string project, Action<string> log, CancellationToken ct) {
        var running = (await docker.ListContainersByLabelsAsync([$"{ComposeProjectLabel}={project}"], ct))
            .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var container in running) {
            log($"Stopping {DisplayName(container)} for a consistent snapshot");
            await docker.StopContainerAsync(container.Id, ct);
        }
        return running;
    }

    /// <summary>
    /// Restarts what <see cref="StopRunningContainersAsync"/> stopped. Not cancellable: containers
    /// must come back even when the backup was aborted, and a restart failure is reported but never
    /// masks the primary outcome.
    /// </summary>
    private async Task RestartContainersAsync(IReadOnlyList<DockerContainerInfo> stopped, Action<string> log) {
        foreach (var container in stopped) {
            try {
                await docker.StartContainerAsync(container.Id, CancellationToken.None);
                log($"Restarted {DisplayName(container)}");
            } catch (Exception ex) {
                log($"WARNING: failed to restart {DisplayName(container)}: {ex.Message}");
                logger.LogError(ex, "Failed to restart container {ContainerId} after backup", container.Id);
            }
        }
    }

    private async Task ApplyRetentionAsync(
        IBackupStorage storage, string directory, BackupOptions backup, Action<string> log, CancellationToken ct) {
        if (backup.RetentionDays <= 0 && backup.RetentionMaxCount <= 0) return;
        try {
            var names = (await storage.ListFilesAsync(directory, ct)).Select(f => f.Name).ToList();
            var deletions = BackupRetention.SelectDeletions(
                names, DateTimeOffset.UtcNow, backup.RetentionDays, backup.RetentionMaxCount);
            foreach (var name in deletions) {
                await storage.DeleteFileAsync($"{directory}/{name}", ct);
                log($"Retention: deleted {name}");
            }
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            // The backup itself succeeded — an unreachable prune retries on the next run.
            log($"WARNING: retention pruning failed: {ex.Message}");
            logger.LogWarning(ex, "Retention pruning failed for {Directory}", directory);
        }
    }

    private static string BuildManifest(
        string instance, Stack stack, IReadOnlyList<string> volumes, DateTimeOffset takenAt, bool encrypted) =>
        new JsonObject {
            ["formatVersion"] = 1,
            ["tool"] = "watchtower",
            ["instance"] = instance,
            ["stackId"] = stack.Id,
            ["stackName"] = stack.Name,
            ["composeProject"] = stack.ComposeProjectName,
            ["volumes"] = new JsonArray([.. volumes.Select(v => JsonValue.Create(v))]),
            ["createdAtUtc"] = takenAt.UtcDateTime.ToString("O"),
            ["encrypted"] = encrypted,
        }.ToJsonString();

    private static string DisplayName(DockerContainerInfo container) =>
        container.Names.FirstOrDefault()?.TrimStart('/') ?? container.Id[..12];

    private async Task FinishAsync(
        int backupEventId, bool success, string output, string? remotePath, long? sizeBytes, CancellationToken ct) {
        // Not the caller's token: the terminal state must be written even when the run was cancelled.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var evt = await db.BackupEvents.FirstOrDefaultAsync(e => e.Id == backupEventId, CancellationToken.None);
        if (evt is null) return;
        evt.Status = success ? "success" : "failed";
        evt.Output = output;
        evt.RemotePath = remotePath;
        evt.SizeBytes = sizeBytes;
        evt.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
