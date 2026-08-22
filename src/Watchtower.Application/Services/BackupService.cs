using System.Formats.Tar;
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
        // Locked: a dependency level is quiesced concurrently, and its tasks all log.
        void Log(string line) { lock (output) output.AppendLine(line); }

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
                $"{RunSummary(triggeredBy, stack, backup, result.StoppedCount, result.ExcludedVolumeCount, result.PausedCount)}"
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
    /// <param name="pausedCount">How many containers the run paused rather than stopped (ADR-0019).</param>
    internal static string RunSummary(
        string trigger, Stack stack, BackupOptions backup, int? stoppedCount = null, int excludedVolumeCount = 0,
        int pausedCount = 0) {
        var provider = backup.ResolveProvider() == BackupProviderKind.Local ? "local" : "sftp";
        var quiesced = (stoppedCount, pausedCount) switch {
            (null, _) when !stack.BackupStopContainers => "",
            (null, _) => stack.BackupQuiesceMode == BackupQuiesceMode.Pause ? " · containers paused" : " · containers stopped",
            ( > 0, > 0) => $" · {pausedCount} container(s) paused, {stoppedCount} stopped",
            (_, > 0) => $" · {pausedCount} container(s) paused",
            ( > 0, _) => $" · {stoppedCount} container(s) stopped",
            _ => "",
        };
        return $"{trigger} · {provider}"
            + (string.IsNullOrEmpty(backup.EncryptionPassphrase) ? "" : " · encrypted")
            + quiesced
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
    /// volumes and extract the archive back into them (ADR-0016), replay every database dump the
    /// archive carries into its running server (ADR-0017 §5), restart. Only volumes present in BOTH
    /// the archive and on the host are touched; mismatches are logged, never guessed at.
    /// </summary>
    public async Task ExecuteRestoreAsync(int backupEventId, string fileName, CancellationToken ct) {
        var output = new StringBuilder();
        // Locked: a dependency level is quiesced concurrently, and its tasks all log.
        void Log(string line) { lock (output) output.AppendLine(line); }

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
                $"{result.RemotePath} · {result.VolumeCount} volume(s) restored"
                + (result.DumpCount > 0 ? $" · {result.DumpCount} dump(s) replayed" : ""),
                ct: CancellationToken.None);
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
        var replaySpools = new List<string>();
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

            // 3. What the host has: the project's containers in every state, and its volumes.
            var containers = await ListProjectContainersAsync(stack.ComposeProjectName, ct);
            var hostVolumes = (await docker.ListVolumesAsync(ct))
                .Where(v => v.Labels is { } labels && labels.TryGetValue(ComposeProjectLabel, out var p)
                    && p == stack.ComposeProjectName)
                .Select(v => v.Name)
                .ToList();

            // 4. Only volumes in both are touched; a volume the archive replaced with a dump is
            // reported as covered rather than as a gap, because leaving it in place is the plan.
            var targets = contents.Volumes.Intersect(hostVolumes, StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var dumpPlan = RestoreDumpPlan.Match(contents, containers);
            foreach (var archiveOnly in contents.Volumes.Except(hostVolumes, StringComparer.Ordinal))
                log($"WARNING: archive volume '{archiveOnly}' does not exist on this host — skipped.");
            foreach (var hostOnly in hostVolumes.Except(contents.Volumes, StringComparer.Ordinal))
                log(dumpPlan.CoveredBy.TryGetValue(hostOnly, out var coveringService)
                    ? $"Volume '{hostOnly}' is covered by the '{coveringService}' dump — left in place."
                    : $"WARNING: host volume '{hostOnly}' is not in the archive — left untouched.");

            // 5. Everything the dumps need has to be true before anything is stopped or wiped: a
            // missing file or a service that is no longer a database refuses the whole restore.
            foreach (var warning in dumpPlan.Warnings) log($"WARNING: {warning}");
            if (dumpPlan.Errors.Count > 0)
                throw new InvalidOperationException(
                    "This archive cannot be restored into the stack as it stands: "
                    + string.Join(" ", dumpPlan.Errors));
            if (dumpPlan.Replays.Count > 0)
                log($"Archive carries {dumpPlan.Replays.Count} dump(s): "
                    + string.Join(", ", dumpPlan.Replays.Select(r =>
                        $"{r.Service} ({r.Engine.ToString().ToLowerInvariant()}, "
                        + $"{r.ExpectedDatabases.Count} database(s))"))
                    + ".");

            // 6. An archive of dumps alone still restores; nothing to do at all does not.
            if (targets.Count == 0 && dumpPlan.Replays.Count == 0)
                throw new InvalidOperationException(
                    "None of the archive's volumes exist on this host. Deploy the stack first so its volumes exist.");

            // 7. Each database has to be up and reachable before the stack goes down — a replay that
            // could never have worked must not cost the stack its volumes first.
            var connections = new Dictionary<string, PostgresConnection>(StringComparer.Ordinal);
            foreach (var replay in dumpPlan.Replays) {
                var container = containers.First(c => string.Equals(c.Id, replay.ContainerId, StringComparison.Ordinal));
                if (!string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase)) {
                    // Left running afterwards on purpose: the restored database is the one thing the
                    // operator will want to look at, and compose brings the rest back anyway.
                    log($"Starting {replay.ContainerName} to replay the '{replay.Service}' dump (it was stopped).");
                    await docker.StartContainerAsync(replay.ContainerId, ct);
                }
                var declared = await postgres.ReadConnectionAsync(replay.ContainerId, ct);
                await postgres.WaitReadyAsync(replay.ContainerId, declared, replay.Service, log, ct);
                connections[replay.ContainerId] = await postgres.PreflightAsync(
                    new DumpTarget(
                        replay.ContainerId, replay.ContainerName, replay.Service, container.Image,
                        replay.Engine, null, []),
                    log, ct);
            }

            // 8. Which volumes the labels allow us to touch, and who has to go down for them. The
            // master switch is passed on regardless of the stack's setting: extracting under a
            // running application is never sound, so a restore always stops the writers it finds.
            // With dumps to replay, everything running goes down, not only the volume writers: a
            // stateless api that merely talks to the database would reconnect between the session
            // terminate and DROP DATABASE, and --clean would merge into the old database.
            // Always a real stop, never a pause: a process thawed over files that were replaced
            // underneath it is no better off than one that kept running through the extraction.
            var plan = Plan(containers, targets, stopContainers: true, log,
                keepRunning: new HashSet<string>(
                    dumpPlan.Replays.Select(r => r.ContainerId), StringComparer.Ordinal),
                stopAllRunning: dumpPlan.Replays.Count > 0,
                forceStop: true);
            foreach (var excluded in plan.Excluded)
                log(excluded.Reason == BackupVolumeExclusionReason.Label
                    ? $"WARNING: archive volume '{excluded.Name}' is excluded by {BackupPlan.ExcludeLabel} "
                        + $"({excluded.Detail}) — left untouched."
                    : $"WARNING: archive volume '{excluded.Name}' is excluded — {excluded.Detail}.");
            if (plan.Volumes.Count == 0 && dumpPlan.Replays.Count == 0)
                throw new InvalidOperationException(
                    $"Every volume of this archive is excluded by {BackupPlan.ExcludeLabel} — nothing to restore.");
            if (plan.Volumes.Count > 0)
                log($"Restoring {plan.Volumes.Count} volume(s): {string.Join(", ", plan.Volumes)}");
            foreach (var kept in plan.Keep.Where(k => k.MountsPlannedVolume))
                log($"WARNING: {kept.Container.DisplayName} mounts a restored volume but "
                    + $"{RestoreKeepReason(kept.Reason)} — extracting underneath it may corrupt the result.");

            var stopped = await QuiescePlannedContainersAsync(plan, stack, backup, log, ct);
            try {
                // 9. Wipe + extract, unless the archive is dumps only.
                if (plan.Volumes.Count > 0) {
                    await WithSpoolTarAsync<object?>(spoolPath, encrypted, backup, async tar => {
                        await archiveService.RestoreArchiveAsync(plan.Volumes, tar, backup.HelperImage, ct);
                        return null;
                    });
                    log("Archive extracted into the volumes.");
                }

                // 10. Replay, with the applications still down so nothing writes into a database
                // that is being dropped and recreated underneath it.
                await ReplayDumpsAsync(dumpPlan, connections, spoolPath, replaySpools, encrypted, backup, log, ct);
            } finally {
                // 11. Back up in dependency order, whatever happened above.
                await ResumeContainersAsync(stopped, log);
            }

            return new RunResult(
                relativePath, sizeBytes, plan.Volumes.Count, stopped.StoppedCount, plan.Excluded.Count,
                dumpPlan.Replays.Count, stopped.PausedCount);
        } finally {
            foreach (var path in replaySpools.Append(spoolPath)) {
                try {
                    if (File.Exists(path)) File.Delete(path);
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to delete restore spool file {SpoolPath}", path);
                }
            }
        }
    }

    /// <summary>
    /// Replays every dump of the archive into its database, with the stack's applications still down.
    /// </summary>
    /// <remarks>
    /// A failing replay does not stop the others: the volumes are already wiped by this point, so the
    /// only useful thing left is to get as much of the stack's state back as possible and then report
    /// every failure at once. The run still fails — a partially restored stack that reported success
    /// is the one outcome an operator cannot act on.
    /// </remarks>
    private async Task ReplayDumpsAsync(
        RestoreDumpPlan dumpPlan,
        IReadOnlyDictionary<string, PostgresConnection> connections,
        string spoolPath,
        List<string> replaySpools,
        bool encrypted,
        BackupOptions backup,
        Action<string> log,
        CancellationToken ct) {
        var failures = new List<string>();
        foreach (var replay in dumpPlan.Replays) {
            var sqlPath = Path.Combine(Path.GetTempPath(), $"watchtower-replay-{Guid.NewGuid():N}.sql");
            replaySpools.Add(sqlPath);
            try {
                var sizeBytes = await ExtractDumpAsync(spoolPath, encrypted, backup, replay.File, sqlPath, ct);
                log($"Replaying the '{replay.Service}' dump ({sizeBytes} bytes)…");
                var result = await postgres.ReplayAsync(
                    replay.ContainerId, connections[replay.ContainerId], replay.Service, sqlPath,
                    replay.ExpectedDatabases, log, ct);
                if (result.ErrorLineCount > 0)
                    log($"WARNING: psql reported {result.ErrorLineCount} diagnostic line(s) replaying "
                        + $"'{replay.Service}'; some are expected from --clean (e.g. role \"postgres\" "
                        + $"already exists). First: {string.Join(" | ", result.SampleErrors.Take(3))}");
                log(replay.ExpectedDatabases.Count > 0
                    ? $"Replayed '{replay.Service}': {replay.ExpectedDatabases.Count}/"
                        + $"{replay.ExpectedDatabases.Count} database(s) present."
                    : $"Replayed '{replay.Service}'.");
            } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
                log($"WARNING: {ex.Message}");
                logger.LogWarning(ex, "Replaying the dump of service {Service} failed", replay.Service);
                failures.Add(ex.Message);
            }
        }
        if (failures.Count == 1) throw new InvalidOperationException(failures[0]);
        if (failures.Count > 1)
            throw new InvalidOperationException(
                $"{failures.Count} dumps could not be replayed: {string.Join(" ", failures)}");
    }

    /// <summary>
    /// Copies one dump out of the archive spool into a host file, so it can be streamed into the
    /// database container. Read straight from the spool (decrypt → gunzip → tar) rather than kept
    /// from the earlier passes: a dump is arbitrarily large and has no business in memory.
    /// </summary>
    /// <returns>The size of the extracted SQL.</returns>
    private static Task<long> ExtractDumpAsync(
        string spoolPath, bool encrypted, BackupOptions backup, string relativeFile, string destination,
        CancellationToken ct) =>
        WithSpoolTarAsync(spoolPath, encrypted, backup, async tar => {
            var wanted = $"backup/{relativeFile}";
            await using var reader = new TarReader(tar, leaveOpen: true);
            while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry) {
                if (!string.Equals(entry.Name.TrimStart('.', '/'), wanted, StringComparison.Ordinal)) continue;
                if (entry.DataStream is not { } data) break;
                await using var file = File.Create(destination);
                await data.CopyToAsync(file, ct);
                return file.Length;
            }
            // The table-of-contents scan found it a moment ago, so this means the archive changed
            // under us or is damaged — either way, not something to restore from.
            throw new InvalidOperationException(
                $"The archive does not contain '{wanted}', although its table of contents lists it.");
        });

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
        int DumpCount = 0, int PausedCount = 0);

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
            excludeVolumes: dumpCovered,
            quiesceMode: stack.BackupQuiesceMode);
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
            var stopped = await QuiescePlannedContainersAsync(plan, stack, backup, log, ct);
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
                await ResumeContainersAsync(stopped, log);
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
                relativePath, sizeBytes, volumes.Count, stopped.StoppedCount, plan.Excluded.Count, dumps.Count,
                stopped.PausedCount);
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
    /// <param name="stopAllRunning">Quiesce every running container (restore with dumps).</param>
    /// <param name="quiesceMode">The stack's default quiesce mode for unlabelled containers.</param>
    /// <param name="forceStop">Stop everything that is quiesced, labels and default notwithstanding (restore).</param>
    private static BackupPlan Plan(
        IReadOnlyList<DockerContainerInfo> containers,
        IReadOnlyList<string> volumes,
        bool stopContainers,
        Action<string> log,
        IReadOnlySet<string>? keepRunning = null,
        IReadOnlyDictionary<string, string>? excludeVolumes = null,
        bool stopAllRunning = false,
        BackupQuiesceMode quiesceMode = BackupQuiesceMode.Stop,
        bool forceStop = false) {
        var plan = BackupPlan.Create(
            containers, volumes, stopContainers, keepRunning, excludeVolumes, stopAllRunning, quiesceMode, forceStop);
        foreach (var warning in plan.Warnings) log($"WARNING: {warning}");
        return plan;
    }

    /// <summary>
    /// What a run actually took down, level by level in the order it happened — the exact set
    /// <see cref="ResumeContainersAsync"/> brings back, so a run that failed part-way never starts a
    /// container it did not stop.
    /// </summary>
    /// <param name="Levels">The quiesced steps, grouped as <see cref="BackupPlan.Levels"/> were, minus the steps that failed.</param>
    /// <param name="RecordedPauseIds">
    /// The container ids <see cref="RecordPausesAsync"/> wrote to the safety-net table for this run —
    /// the planned pauses, whether or not each was reached. Cleared on resume for every container that
    /// is not left paused.
    /// </param>
    internal sealed record QuiescedContainers(
        IReadOnlyList<IReadOnlyList<BackupQuiesceStep>> Levels,
        IReadOnlyCollection<string> RecordedPauseIds) {
        public int Count => Levels.Sum(level => level.Count);
        public int StoppedCount => Levels.Sum(level => level.Count(s => s.Mode == BackupQuiesceMode.Stop));
        public int PausedCount => Levels.Sum(level => level.Count(s => s.Mode == BackupQuiesceMode.Pause));
    }

    /// <summary>
    /// Takes down what the plan selected — each dependency level at once, levels in order — and returns
    /// exactly what went down so the caller can bring it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Within a level nothing depends on anything else, so its stops and pauses are issued concurrently:
    /// the window for a level is its slowest container, not the sum. Stops carry the short
    /// <see cref="BackupOptions.StopTimeoutSeconds"/> (<c>?t=</c>) rather than the daemon's 10 s, and a
    /// pause is a cgroup freeze that returns in milliseconds.
    /// </para>
    /// <para>
    /// A level that fails part-way resumes what is already down — across all levels so far — before
    /// rethrowing. Without that, a daemon hiccup on the third of five containers would leave the first
    /// two stopped with nobody holding their list, and a paused one frozen: the stack would stay
    /// half-down until an operator noticed. The pauses are additionally written to
    /// <see cref="BackupPausedContainer"/> <em>before</em> they happen, so a Watchtower that dies inside
    /// the window can thaw them on its next start (<see cref="UnpauseLeftoversAsync"/>).
    /// </para>
    /// </remarks>
    internal async Task<QuiescedContainers> QuiescePlannedContainersAsync(
        BackupPlan plan, Stack stack, BackupOptions backup, Action<string> log, CancellationToken ct) {
        var stopping = plan.Quiesce.Where(s => s.Mode == BackupQuiesceMode.Stop).Select(s => s.Container.DisplayName).ToList();
        var pausing = plan.Quiesce.Where(s => s.Mode == BackupQuiesceMode.Pause).Select(s => s.Container.DisplayName).ToList();
        if (plan.Quiesce.Count > 0 || plan.Keep.Count > 0)
            log($"Quiescing {plan.Quiesce.Count} of {plan.Quiesce.Count + plan.Keep.Count} running container(s)"
                + (stopping.Count > 0 ? $": stopping {string.Join(", ", stopping)}" : "")
                + (pausing.Count > 0 ? $"{(stopping.Count > 0 ? ";" : ":")} pausing {string.Join(", ", pausing)}" : "")
                + (plan.Keep.Count > 0
                    ? $"; leaving {string.Join(", ", plan.Keep.Select(k => k.Container.DisplayName))} up"
                    : "")
                + (plan.Levels.Count > 1 ? $" — {plan.Levels.Count} dependency levels" : "")
                + ".");

        // The safety net is written first, outside the window: a crash after this line and before the
        // pause leaves a row whose container is not paused, which the reconcile simply drops.
        var recorded = await RecordPausesAsync(plan, stack, ct);
        var stopTimeout = backup.ResolveStopTimeoutSeconds();
        var levels = new List<IReadOnlyList<BackupQuiesceStep>>(plan.Levels.Count);
        try {
            foreach (var level in plan.Levels) {
                var tasks = level.Select(step => QuiesceOneAsync(step, stopTimeout, log, ct)).ToList();
                try {
                    await Task.WhenAll(tasks);
                } finally {
                    // Whatever did go down in this level is remembered, even when a sibling failed.
                    var done = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToList();
                    if (done.Count > 0) levels.Add(done);
                }
            }
        } catch {
            await ResumeContainersAsync(new QuiescedContainers(levels, recorded), log);
            throw;
        }
        return new QuiescedContainers(levels, recorded);
    }

    /// <summary>One step of a level: the stop (with the short timeout) or the pause.</summary>
    private async Task<BackupQuiesceStep> QuiesceOneAsync(
        BackupQuiesceStep step, int stopTimeoutSeconds, Action<string> log, CancellationToken ct) {
        var container = step.Container;
        if (step.Mode == BackupQuiesceMode.Pause) {
            log($"Pausing {container.DisplayName} for a crash-consistent snapshot");
            await docker.PauseContainerAsync(container.Id, ct);
        } else {
            log($"Stopping {container.DisplayName} for a consistent snapshot (SIGTERM, {stopTimeoutSeconds} s grace)");
            await docker.StopContainerAsync(container.Id, stopTimeoutSeconds, ct);
        }
        return step;
    }

    /// <summary>
    /// Brings back what <see cref="QuiescePlannedContainersAsync"/> took down, levels in reverse — the
    /// quiesce order runs dependents-first, so reversing it starts dependencies first — and, within a
    /// level, concurrently. Not cancellable: containers must come back even when the run was aborted,
    /// and a failure here is reported but never masks the primary outcome. Finally the safety-net rows
    /// are cleared for every container that is not left paused; a container whose unpause failed keeps
    /// its row, so the next start retries it.
    /// </summary>
    internal async Task ResumeContainersAsync(QuiescedContainers quiesced, Action<string> log) {
        var stillPaused = new HashSet<string>(StringComparer.Ordinal);
        for (var i = quiesced.Levels.Count - 1; i >= 0; i--) {
            await Task.WhenAll(quiesced.Levels[i].Select(async step => {
                var container = step.Container;
                try {
                    if (step.Mode == BackupQuiesceMode.Pause) {
                        await docker.UnpauseContainerAsync(container.Id, CancellationToken.None);
                        log($"Unpaused {container.DisplayName}");
                    } else {
                        await docker.StartContainerAsync(container.Id, CancellationToken.None);
                        log($"Restarted {container.DisplayName}");
                    }
                } catch (Exception ex) {
                    if (step.Mode == BackupQuiesceMode.Pause) lock (stillPaused) stillPaused.Add(container.Id);
                    var verb = step.Mode == BackupQuiesceMode.Pause ? "unpause" : "restart";
                    log($"WARNING: failed to {verb} {container.DisplayName}: {ex.Message}");
                    logger.LogError(ex, "Failed to {Verb} container {ContainerId} after backup", verb, container.Id);
                }
            }));
        }
        if (quiesced.RecordedPauseIds.Count > 0)
            await ForgetPausesAsync(quiesced.RecordedPauseIds.Where(id => !stillPaused.Contains(id)).ToList());
    }

    /// <summary>Writes the run's planned pauses to the safety-net table; returns the ids written.</summary>
    private async Task<IReadOnlyCollection<string>> RecordPausesAsync(BackupPlan plan, Stack stack, CancellationToken ct) {
        var pauses = plan.Quiesce.Where(s => s.Mode == BackupQuiesceMode.Pause).ToList();
        if (pauses.Count == 0) return [];
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.BackupPausedContainers.AddRange(pauses.Select(s => new BackupPausedContainer {
            ContainerId = s.Container.Id,
            ContainerName = s.Container.DisplayName,
            StackName = stack.Name,
            PausedAt = now,
        }));
        await db.SaveChangesAsync(ct);
        return [.. pauses.Select(s => s.Container.Id)];
    }

    /// <summary>Clears the safety-net rows of containers that are running again (or never were paused).</summary>
    private async Task ForgetPausesAsync(IReadOnlyCollection<string> containerIds) {
        if (containerIds.Count == 0) return;
        try {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.BackupPausedContainers
                .Where(p => containerIds.Contains(p.ContainerId))
                .ExecuteDeleteAsync(CancellationToken.None);
        } catch (Exception ex) {
            // Harmless leftovers: the next start inspects them, finds them running, and drops them.
            logger.LogWarning(ex, "Failed to clear the paused-container safety net after a backup");
        }
    }

    /// <summary>
    /// The startup half of the pause safety net: unpauses every container a previous process paused
    /// for a backup and did not get to unpause (crash, SIGKILL, power loss mid-window), then clears the
    /// table. A container that is not paused any more — the run died before pausing it, or someone
    /// resumed it by hand — is forgotten; one that no longer exists likewise.
    /// </summary>
    /// <returns>How many containers were unpaused.</returns>
    /// <exception cref="Exception">
    /// Rethrown from the engine when a container could not be inspected or unpaused; its row stays so
    /// the caller can retry — a frozen stack is exactly the state this must not give up on.
    /// </exception>
    public async Task<int> UnpauseLeftoversAsync(CancellationToken ct) {
        List<BackupPausedContainer> rows;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            rows = await db.BackupPausedContainers.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        }
        if (rows.Count == 0) return 0;

        var unpaused = new List<BackupPausedContainer>();
        var settled = new List<int>();
        Exception? failure = null;
        foreach (var row in rows) {
            try {
                string? status;
                try {
                    status = (await docker.InspectContainerAsync(row.ContainerId, ct)).State?.Status;
                } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
                    status = null; // gone — nothing left to thaw
                }
                if (string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase)) {
                    await docker.UnpauseContainerAsync(row.ContainerId, ct);
                    logger.LogWarning(
                        "Unpaused container {ContainerName} of stack {StackName}: a previous process paused it for a backup at {PausedAt:O} and did not resume it",
                        row.ContainerName, row.StackName, row.PausedAt);
                    unpaused.Add(row);
                }
                settled.Add(row.Id);
            } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
                logger.LogError(ex, "Could not reconcile paused container {ContainerName} ({ContainerId})", row.ContainerName, row.ContainerId);
                failure ??= ex;
            }
        }

        if (settled.Count > 0) {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.BackupPausedContainers.Where(p => settled.Contains(p.Id)).ExecuteDeleteAsync(ct);
        }
        if (unpaused.Count > 0)
            await audit.RecordAsync(AuditCategory, "reconcile.unpause",
                string.Join(", ", unpaused.Select(r => r.StackName).Distinct(StringComparer.Ordinal)),
                $"unpaused {unpaused.Count} container(s) left paused by an interrupted backup: "
                + string.Join(", ", unpaused.Select(r => r.ContainerName)),
                ct: CancellationToken.None);
        if (failure is not null) throw failure;
        return unpaused.Count;
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
