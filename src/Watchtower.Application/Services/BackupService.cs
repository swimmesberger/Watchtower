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
/// Executes one stack backup end to end (ADR-0016): resolve the stack's compose volumes, stop the
/// containers that write to them for a consistent snapshot, spool the (gzipped, optionally encrypted)
/// volume archive to a temp file, restart the containers, upload, then apply retention. Progress and
/// the outcome are recorded on the run's <see cref="BackupEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// Singleton driven by <see cref="BackupQueueService"/>'s worker; reaches the scoped DbContext
/// through <see cref="IServiceScopeFactory"/> (ADR-0004). The archive is spooled locally
/// <em>before</em> the upload so the container-stop window covers only the snapshot, never the
/// (possibly slow) network transfer.
/// </para>
/// <para>
/// Which containers go down is not this class's decision: <see cref="BackupPlan"/> computes the stop
/// set from what each container mounts, the per-service <c>watchtower.backup.*</c> labels and Compose's
/// <c>depends_on</c> graph (dependents stop first, dependencies restart first). The stack's "stop
/// containers" switch remains the master override — off means nothing is stopped. This service only
/// executes the plan and reports it.
/// </para>
/// </remarks>
public sealed class BackupService(
    DockerEngineClient docker,
    BackupArchiveService archiveService,
    PostgresDumpService postgres,
    BackupStorageFactory storageFactory,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    AuditLog audit,
    ILogger<BackupService> logger) {

    /// <summary>The category the backup plane records under in the general audit trail.</summary>
    internal const string AuditCategory = "backups";

    /// <summary>The compose label a stack's volumes carry.</summary>
    private const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>Runs the backup for an event created by <see cref="BackupQueueService.Enqueue"/>.</summary>
    public async Task ExecuteBackupAsync(int backupEventId, CancellationToken ct) {
        var output = new StringBuilder();
        void Log(string line) => output.AppendLine(line);

        int stackId;
        string triggeredBy;
        Stack? stack;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var evt = await db.BackupEvents.FirstOrDefaultAsync(e => e.Id == backupEventId, ct);
            if (evt is null) return; // stack (and its events) deleted while queued
            stackId = evt.StackId;
            triggeredBy = evt.TriggeredBy;
            stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stackId, ct);
            evt.Status = "running";
            evt.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (stack is null) {
            await FinishAsync(backupEventId, success: false, "Stack no longer exists.", null, null, ct);
            return;
        }

        var backup = options.CurrentValue.Backup;
        try {
            var result = await RunAsync(stack, backup, Log, ct);
            await FinishAsync(backupEventId, success: true, output.ToString(), result.RemotePath, result.SizeBytes, ct);
            // The audit row carries the settings the run operated under, so "was it encrypted back
            // then?" is answered by the trail, not by today's configuration.
            await audit.RecordAsync(AuditCategory, "run", stack.Name,
                $"{RunSummary(triggeredBy, stack, backup, result.StoppedCount, result.ExcludedVolumeCount)}"
                + $" · {result.VolumeCount} volume(s)"
                + (result.DumpCount > 0 ? $" · {result.DumpCount} dump(s)" : "")
                + $", {result.SizeBytes} bytes → {result.RemotePath}",
                ct: CancellationToken.None);
            logger.LogInformation(
                "Backup of stack {StackName} completed: {RemotePath} ({SizeBytes} bytes)",
                stack.Name, result.RemotePath, result.SizeBytes);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            Log($"FAILED: {ex.Message}");
            await FinishAsync(backupEventId, success: false, output.ToString(), null, null, ct);
            await audit.RecordAsync(AuditCategory, "run", stack.Name, RunSummary(triggeredBy, stack, backup),
                success: false, error: ex.Message, ct: CancellationToken.None);
            logger.LogWarning(ex, "Backup of stack {StackName} failed", stack.Name);
        }
    }

    /// <summary>The effective settings a run operated under, for its audit row. Never includes secrets.</summary>
    /// <param name="trigger">Who or what started the run.</param>
    /// <param name="stack">The stack, for its "stop containers" setting.</param>
    /// <param name="backup">The backup options the run operated under.</param>
    /// <param name="stoppedCount">
    /// How many containers the run actually stopped, once that is known. Null on the failure path, where
    /// the run may not have reached its stop step — the row then reports the setting rather than a count
    /// it cannot vouch for.
    /// </param>
    /// <param name="excludedVolumeCount">How many candidate volumes were left out of the archive.</param>
    internal static string RunSummary(
        string trigger, Stack stack, BackupOptions backup, int? stoppedCount = null, int excludedVolumeCount = 0) {
        var provider = backup.ResolveProvider() == BackupProviderKind.Local ? "local" : "sftp";
        var stopped = stoppedCount switch {
            null => stack.BackupStopContainers ? " · containers stopped" : "",
            > 0 => $" · {stoppedCount} container(s) stopped",
            _ => "",
        };
        return $"{trigger} · {provider}"
            + (string.IsNullOrEmpty(backup.EncryptionPassphrase) ? "" : " · encrypted")
            + stopped
            + (excludedVolumeCount > 0 ? $" · {excludedVolumeCount} volume(s) excluded" : "")
            + $" · {RetentionSummary(backup)}";
    }

    /// <summary>Human-readable retention policy, matching the Settings card's two knobs.</summary>
    internal static string RetentionSummary(BackupOptions backup) =>
        (backup.RetentionDays, backup.RetentionMaxCount) switch {
            (0, 0) => "keep forever",
            (var days, 0) => $"retention {days}d",
            (0, var count) => $"keep {count}",
            var (days, count) => $"retention {days}d, keep {count}",
        };

    /// <summary>
    /// Runs a restore enqueued by <see cref="BackupQueueService.TryEnqueueRestore"/>: download the
    /// archive, scan its table of contents, stop the containers that mount a target volume, wipe those
    /// volumes and extract the archive back into them (ADR-0016), restart. Only volumes present in BOTH
    /// the archive and on the host are touched; mismatches are logged, never guessed at.
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
            await audit.RecordAsync(AuditCategory, "restore", stack.Name,
                $"{result.RemotePath} · {result.VolumeCount} volume(s) restored", ct: CancellationToken.None);
            logger.LogInformation("Restore of {RemotePath} into stack {StackName} completed", result.RemotePath, stack.Name);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            Log($"FAILED: {ex.Message}");
            await FinishAsync(backupEventId, success: false, output.ToString(), null, null, ct);
            await audit.RecordAsync(AuditCategory, "restore", stack.Name, fileName,
                success: false, error: ex.Message, ct: CancellationToken.None);
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

            // 3. Which of those the labels actually allow us to touch, and who has to go down for them.
            // The master switch is passed on regardless of the stack's setting: extracting under a
            // running application is never sound, so a restore always stops the writers it finds.
            var containers = await ListProjectContainersAsync(stack.ComposeProjectName, ct);
            var plan = Plan(containers, targets, stopContainers: true, log);
            foreach (var excluded in plan.Excluded)
                log(excluded.Reason == BackupVolumeExclusionReason.Label
                    ? $"WARNING: archive volume '{excluded.Name}' is excluded by {BackupPlan.ExcludeLabel} "
                        + $"({excluded.Detail}) — left untouched."
                    : $"WARNING: archive volume '{excluded.Name}' is excluded — {excluded.Detail}.");
            if (plan.Volumes.Count == 0)
                throw new InvalidOperationException(
                    $"Every volume of this archive is excluded by {BackupPlan.ExcludeLabel} — nothing to restore.");
            log($"Restoring {plan.Volumes.Count} volume(s): {string.Join(", ", plan.Volumes)}");
            foreach (var kept in plan.Keep.Where(k => k.MountsPlannedVolume))
                log($"WARNING: {kept.Container.DisplayName} mounts a restored volume but "
                    + $"{RestoreKeepReason(kept.Reason)} — extracting underneath it may corrupt the result.");

            // 4. Stop, wipe + extract, restart.
            var stopped = await StopPlannedContainersAsync(plan, log, ct);
            try {
                await WithSpoolTarAsync<object?>(spoolPath, encrypted, backup, async tar => {
                    await archiveService.RestoreArchiveAsync(plan.Volumes, tar, backup.HelperImage, ct);
                    return null;
                });
                log("Archive extracted into the volumes.");
            } finally {
                await RestartContainersAsync(stopped, log);
            }

            return new RunResult(relativePath, sizeBytes, plan.Volumes.Count, stopped.Count, plan.Excluded.Count);
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

    /// <summary>Why a container that mounts a volume being restored was nevertheless left running.</summary>
    private static string RestoreKeepReason(BackupKeepReason reason) => reason switch {
        BackupKeepReason.StopLabel => $"is labelled {BackupPlan.StopLabel}=false",
        BackupKeepReason.Excluded => $"is labelled {BackupPlan.ExcludeLabel}=true",
        BackupKeepReason.CallerRequested => "has to stay up for its dump to be replayed",
        _ => "was left running",
    };

    private sealed record RunResult(
        string RemotePath, long SizeBytes, int VolumeCount, int StoppedCount = 0, int ExcludedVolumeCount = 0,
        int DumpCount = 0);

    /// <summary>One completed database dump, as the manifest records it (ADR-0017).</summary>
    /// <param name="Service">The compose service the dump was taken from — its identity on restore.</param>
    /// <param name="Engine">Which engine's tooling produced it.</param>
    /// <param name="File">Path inside the archive, relative to <c>backup/</c>.</param>
    /// <param name="Image">The image the service was running when it was dumped.</param>
    /// <param name="User">The database role the dump was taken as.</param>
    /// <param name="Container">The container's name at the time of the run.</param>
    /// <param name="Volumes">The volumes this dump stands in for — left out of the file snapshot.</param>
    /// <param name="Databases">The databases the dump covers.</param>
    /// <param name="SizeBytes">Size of the uncompressed SQL.</param>
    internal sealed record BackupDumpEntry(
        string Service,
        DumpEngine Engine,
        string File,
        string Image,
        string User,
        string Container,
        IReadOnlyList<string> Volumes,
        IReadOnlyList<string> Databases,
        long SizeBytes);

    private async Task<RunResult> RunAsync(
        Stack stack, BackupOptions backup, Action<string> log, CancellationToken ct) {
        var project = stack.ComposeProjectName;
        var takenAt = DateTimeOffset.UtcNow;
        var encrypted = !string.IsNullOrEmpty(backup.EncryptionPassphrase);

        // 1. The stack's candidate volumes, by compose project label, and its containers.
        var candidates = (await docker.ListVolumesAsync(ct))
            .Where(v => v.Labels is { } labels && labels.TryGetValue(ComposeProjectLabel, out var p) && p == project)
            .Select(v => v.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var containers = await ListProjectContainersAsync(project, ct);

        // 2. Which databases are captured as a logical dump instead of a file snapshot. They keep
        // running (a dump is consistent without stopping anything) and their data volume leaves the
        // archive, because the dump is the better copy of exactly that content.
        var dumpTargets = await SelectDumpTargetsAsync(containers, log, ct);
        if (candidates.Count == 0 && dumpTargets.Count == 0)
            throw new InvalidOperationException(
                $"No volumes found for compose project '{project}'. Has the stack been deployed?");

        // 3. Narrow the volumes to what the labels allow, and work out who has to go down for them.
        var dumpCovered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in dumpTargets.Where(t => t.DataVolume is not null))
            dumpCovered[target.DataVolume!] = $"covered by the '{target.Service}' dump";
        var plan = Plan(containers, candidates, stack.BackupStopContainers, log,
            keepRunning: new HashSet<string>(dumpTargets.Select(t => t.ContainerId), StringComparer.Ordinal),
            excludeVolumes: dumpCovered);
        foreach (var excluded in plan.Excluded)
            log(excluded.Reason == BackupVolumeExclusionReason.Label
                ? $"Excluding volume '{excluded.Name}' — only mounted by excluded {excluded.Detail}."
                : $"Excluding volume '{excluded.Name}' — {excluded.Detail}.");
        if (plan.Volumes.Count == 0 && dumpTargets.Count == 0)
            throw new InvalidOperationException(
                $"Every volume of compose project '{project}' is excluded by {BackupPlan.ExcludeLabel} "
                + "— there is nothing left to archive.");
        var volumes = plan.Volumes;
        log(volumes.Count == 0
            ? $"No volumes left to snapshot for project '{project}' — the archive carries the dump(s) alone."
            : $"Backing up {volumes.Count} volume(s) of project '{project}': {string.Join(", ", volumes)}");
        foreach (var kept in plan.Keep.Where(k =>
            k.MountsPlannedVolume && k.Reason != BackupKeepReason.MasterSwitchOff))
            log($"WARNING: {kept.Container.DisplayName} keeps running while volume(s) it writes to are "
                + "archived — that volume's snapshot is only crash-consistent.");

        // 4. Prove every dump can be taken while the stack is still fully up: a database we cannot
        // reach has to fail the run here, not after its dependents have been stopped.
        var connections = new Dictionary<string, PostgresConnection>(StringComparer.Ordinal);
        foreach (var target in dumpTargets)
            connections[target.ContainerId] = await postgres.PreflightAsync(target, log, ct);

        var instance = backup.ResolveInstanceName();
        var directory = BackupNaming.StackDirectory(instance, stack.Name);
        var fileName = BackupNaming.FileName(project, takenAt, encrypted);
        var relativePath = $"{directory}/{fileName}";

        // 5. Snapshot to a local spool file — stopping containers only for this step, not the upload.
        var spoolPath = Path.Combine(Path.GetTempPath(), $"watchtower-{fileName}.spool");
        var dumpSpools = new List<string>();
        try {
            var stopped = await StopPlannedContainersAsync(plan, log, ct);
            var dumps = new List<BackupDumpEntry>();
            try {
                // The dumps are taken inside the stop window, so the SQL and the file snapshots
                // describe one moment of the stack rather than two.
                var extras = new List<BackupExtraFile>();
                var usedNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var target in dumpTargets) {
                    var dumpSpool = Path.Combine(Path.GetTempPath(), $"watchtower-dump-{Guid.NewGuid():N}.sql");
                    dumpSpools.Add(dumpSpool);
                    var connection = connections[target.ContainerId];
                    var dumped = await postgres.DumpAsync(target, connection, dumpSpool, log, ct);
                    var file = $"{PostgresDumpService.DumpDirectory}/{DumpFileName(target.Service, usedNames)}";
                    extras.Add(new BackupExtraFile(file, dumpSpool));
                    dumps.Add(new BackupDumpEntry(
                        target.Service, target.Engine, file, target.Image, connection.User, target.ContainerName,
                        target.DataVolume is { } data ? [data] : [], dumped.Databases, dumped.SizeBytes));
                }

                var manifest = BuildManifest(instance, stack, volumes, takenAt, encrypted, dumps);
                await using var spool = File.Create(spoolPath);
                Stream sink = spool;
                try {
                    if (encrypted)
                        sink = BackupEncryption.CreateEncryptingStream(spool, backup.EncryptionPassphrase!);
                    await using (var gzip = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true))
                        await archiveService.WriteArchiveAsync(
                            volumes, manifest, extras, gzip, backup.HelperImage, ct);
                } finally {
                    if (!ReferenceEquals(sink, spool)) await sink.DisposeAsync(); // flush final cipher block
                }
            } finally {
                await RestartContainersAsync(stopped, log);
            }

            var sizeBytes = new FileInfo(spoolPath).Length;
            log($"Snapshot complete: {sizeBytes} bytes{(encrypted ? " (encrypted)" : "")}");

            // 6. Upload + retention.
            using var storage = storageFactory.Create(backup);
            log($"Uploading to {storage.Description}: {relativePath}");
            await storage.UploadAsync(relativePath, async (dest, uploadCt) => {
                await using var read = File.OpenRead(spoolPath);
                await read.CopyToAsync(dest, uploadCt);
            }, ct);

            await ApplyRetentionAsync(storage, directory, backup, log, ct);
            return new RunResult(
                relativePath, sizeBytes, volumes.Count, stopped.Count, plan.Excluded.Count, dumps.Count);
        } finally {
            foreach (var path in dumpSpools.Append(spoolPath)) {
                try {
                    if (File.Exists(path)) File.Delete(path);
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to delete backup spool file {SpoolPath}", path);
                }
            }
        }
    }

    /// <summary>
    /// The archive-side file name for a service's dump: its sanitized name, with a numeric suffix when
    /// two services sanitize to the same thing (<c>my.db</c> and <c>my-db</c> both become <c>my-db</c>).
    /// </summary>
    private static string DumpFileName(string service, HashSet<string> used) {
        var stem = BackupNaming.Sanitize(service);
        var name = $"{stem}.sql";
        for (var suffix = 2; !used.Add(name); suffix++) name = $"{stem}-{suffix}.sql";
        return name;
    }

    /// <summary>
    /// Works out which containers are dumped rather than snapshotted, resolving <c>PGDATA</c> for the
    /// candidates only — inspecting every container of a project to find its one database would be a
    /// round-trip per service on every run.
    /// </summary>
    private async Task<IReadOnlyList<DumpTarget>> SelectDumpTargetsAsync(
        IReadOnlyList<DockerContainerInfo> containers, Action<string> log, CancellationToken ct) {
        var pgData = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var candidate in DatabaseDumpTargets.Candidates(containers)) {
            try {
                var details = await docker.InspectContainerAsync(candidate.Id, ct);
                pgData[candidate.Id] = (details.Config.Env ?? [])
                    .FirstOrDefault(e => e.StartsWith("PGDATA=", StringComparison.Ordinal))?["PGDATA=".Length..];
            } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
                // Only costs us the volume exclusion: the default data directory is assumed, and a
                // volume that is really the data directory is then archived as well as dumped.
                log($"WARNING: could not read the environment of {candidate.Names?.FirstOrDefault()?.TrimStart('/')
                    ?? candidate.Id} ({ex.Message}) — assuming the default data directory.");
                logger.LogWarning(ex, "Failed to inspect dump candidate {ContainerId}", candidate.Id);
            }
        }
        return DatabaseDumpTargets.Select(containers, pgData, log);
    }

    /// <summary>
    /// The compose project's containers in every state — a stopped service still owns its volumes, and
    /// an excluded one that happens to be down must not put its volume back into the archive.
    /// </summary>
    private Task<IReadOnlyList<DockerContainerInfo>> ListProjectContainersAsync(
        string project, CancellationToken ct) =>
        docker.ListContainersByLabelsAsync([$"{ComposeProjectLabel}={project}"], ct);

    /// <summary>
    /// Turns the project's containers, the candidate volumes and the stack's master switch into a
    /// <see cref="BackupPlan"/>, surfacing the planner's warnings in the run output.
    /// </summary>
    /// <param name="containers">Every container of the project, as the engine listed them.</param>
    /// <param name="volumes">The candidate volumes.</param>
    /// <param name="stopContainers">The stack's "stop containers" master switch.</param>
    /// <param name="log">The run output.</param>
    /// <param name="keepRunning">Containers the run needs left up — the databases it dumps.</param>
    /// <param name="excludeVolumes">Volumes captured another way, name → reason detail.</param>
    private static BackupPlan Plan(
        IReadOnlyList<DockerContainerInfo> containers,
        IReadOnlyList<string> volumes,
        bool stopContainers,
        Action<string> log,
        IReadOnlySet<string>? keepRunning = null,
        IReadOnlyDictionary<string, string>? excludeVolumes = null) {
        var plan = BackupPlan.Create(containers, volumes, stopContainers, keepRunning, excludeVolumes);
        foreach (var warning in plan.Warnings) log($"WARNING: {warning}");
        return plan;
    }

    /// <summary>
    /// Stops what the plan selected, in the plan's order; returns the containers actually stopped, in
    /// that same order, so the caller can bring exactly those back.
    /// </summary>
    /// <remarks>
    /// A stop that fails part-way through restarts what is already down before rethrowing. Without that,
    /// a daemon hiccup on the third of five containers would leave the first two stopped with nobody
    /// holding their list — the stack would stay half-down until an operator noticed.
    /// </remarks>
    private async Task<IReadOnlyList<BackupContainer>> StopPlannedContainersAsync(
        BackupPlan plan, Action<string> log, CancellationToken ct) {
        if (plan.Stop.Count > 0 || plan.Keep.Count > 0)
            log($"Stopping {plan.Stop.Count} of {plan.Stop.Count + plan.Keep.Count} running container(s)"
                + (plan.Stop.Count > 0 ? $": {string.Join(", ", plan.Stop.Select(c => c.DisplayName))}" : "")
                + (plan.Keep.Count > 0
                    ? $"; leaving {string.Join(", ", plan.Keep.Select(k => k.Container.DisplayName))} up"
                    : "")
                + ".");

        var stopped = new List<BackupContainer>(plan.Stop.Count);
        try {
            foreach (var container in plan.Stop) {
                log($"Stopping {container.DisplayName} for a consistent snapshot");
                await docker.StopContainerAsync(container.Id, ct);
                stopped.Add(container);
            }
        } catch {
            await RestartContainersAsync(stopped, log);
            throw;
        }
        return stopped;
    }

    /// <summary>
    /// Restarts what <see cref="StopPlannedContainersAsync"/> stopped, in reverse — the stop order runs
    /// dependents-first, so reversing it starts dependencies first. Not cancellable: containers must
    /// come back even when the run was aborted, and a restart failure is reported but never masks the
    /// primary outcome.
    /// </summary>
    private async Task RestartContainersAsync(IReadOnlyList<BackupContainer> stopped, Action<string> log) {
        for (var i = stopped.Count - 1; i >= 0; i--) {
            var container = stopped[i];
            try {
                await docker.StartContainerAsync(container.Id, CancellationToken.None);
                log($"Restarted {container.DisplayName}");
            } catch (Exception ex) {
                log($"WARNING: failed to restart {container.DisplayName}: {ex.Message}");
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
            if (deletions.Count > 0) {
                // A retention change can prune a large backlog at once — cap the listing so one
                // pathological pass cannot bloat the audit row.
                var listed = string.Join(", ", deletions.Take(10));
                await audit.RecordAsync(AuditCategory, "retention.prune", directory,
                    $"{RetentionSummary(backup)} · deleted {deletions.Count} archive(s): {listed}"
                    + (deletions.Count > 10 ? ", …" : ""),
                    ct: CancellationToken.None);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            // The backup itself succeeded — an unreachable prune retries on the next run.
            log($"WARNING: retention pruning failed: {ex.Message}");
            await audit.RecordAsync(AuditCategory, "retention.prune", directory, RetentionSummary(backup),
                success: false, error: ex.Message, ct: CancellationToken.None);
            logger.LogWarning(ex, "Retention pruning failed for {Directory}", directory);
        }
    }

    /// <summary>
    /// The archive's self-description, written to <c>backup/backup-manifest.json</c>.
    /// </summary>
    /// <remarks>
    /// <c>formatVersion</c> steps to 2 <em>only</em> when the archive carries dumps, and the
    /// <c>dumps</c> key is then appended at the end: a stack without a database produces a manifest
    /// byte-identical to the one Watchtower wrote before dumps existed, so nothing downstream has to
    /// tell "new tool" from "new archive shape".
    /// </remarks>
    /// <param name="instance">The Watchtower instance name the run belongs to.</param>
    /// <param name="stack">The stack that was backed up.</param>
    /// <param name="volumes">The volumes actually in the archive.</param>
    /// <param name="takenAt">When the run started.</param>
    /// <param name="encrypted">Whether the archive is encrypted.</param>
    /// <param name="dumps">The database dumps the archive carries; empty for a v1 manifest.</param>
    internal static string BuildManifest(
        string instance, Stack stack, IReadOnlyList<string> volumes, DateTimeOffset takenAt, bool encrypted,
        IReadOnlyList<BackupDumpEntry> dumps) {
        var manifest = new JsonObject {
            ["formatVersion"] = dumps.Count > 0 ? 2 : 1,
            ["tool"] = "watchtower",
            ["instance"] = instance,
            ["stackId"] = stack.Id,
            ["stackName"] = stack.Name,
            ["composeProject"] = stack.ComposeProjectName,
            ["volumes"] = new JsonArray([.. volumes.Select(v => JsonValue.Create(v))]),
            ["createdAtUtc"] = takenAt.UtcDateTime.ToString("O"),
            ["encrypted"] = encrypted,
        };
        if (dumps.Count > 0)
            manifest["dumps"] = new JsonArray([.. dumps.Select(DumpNode)]);
        return manifest.ToJsonString();
    }

    /// <summary>One entry of the manifest's <c>dumps</c> array.</summary>
    private static JsonNode DumpNode(BackupDumpEntry dump) => new JsonObject {
        ["service"] = dump.Service,
        ["engine"] = dump.Engine.ToString().ToLowerInvariant(),
        ["file"] = dump.File,
        ["image"] = dump.Image,
        ["user"] = dump.User,
        ["container"] = dump.Container,
        ["volumes"] = new JsonArray([.. dump.Volumes.Select(v => JsonValue.Create(v))]),
        ["databases"] = new JsonArray([.. dump.Databases.Select(d => JsonValue.Create(d))]),
        ["sizeBytes"] = dump.SizeBytes,
    };

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
