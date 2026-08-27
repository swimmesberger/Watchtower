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

/// <summary>What one finished instance archive is, for the caller that has to name or ship it.</summary>
/// <param name="RelativePath">Provider-relative path the archive was uploaded to.</param>
/// <param name="FileName">The archive's file name alone.</param>
/// <param name="Directory">The provider-relative directory it lives in.</param>
/// <param name="SizeBytes">Size of the uploaded archive, after compression and encryption.</param>
/// <param name="TakenAt">When the run started — the timestamp in the file name.</param>
/// <param name="Databases">The databases the dump covers.</param>
public sealed record InstanceArchiveResult(
    string RelativePath, string FileName, string Directory, long SizeBytes,
    DateTimeOffset TakenAt, IReadOnlyList<string> Databases);

/// <summary>
/// Backs up Watchtower's own PostgreSQL (ADR-0027): a logical <c>pg_dumpall</c> of the database every
/// piece of Watchtower state lives in since ADR-0024, wrapped in the same archive format, encryption and
/// storage the stack backups use, and written to <see cref="BackupNaming.InstanceDirectory"/> beside
/// them. One storage folder per instance therefore holds everything a rebuild needs.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is stopped or paused. <c>pg_dumpall</c> is consistent by construction, so Watchtower keeps
/// serving through its own backup — which it must, since it is the thing running the backup. The archive
/// carries no volumes at all: since ADR-0024 the container's <c>/data</c> holds nothing Watchtower needs,
/// and the dump is the whole of the state.
/// </para>
/// <para>
/// Encryption is <em>required</em> here, unlike for a stack. <c>pg_dumpall</c> writes every database
/// role's password hash into the SQL, and the tables it carries include the data-protection key ring,
/// the identity signing key and every certificate's private key. An unencrypted copy of that on a backup
/// target is a worse outcome than no backup at all, so a run without a passphrase is refused rather than
/// quietly downgraded.
/// </para>
/// <para>
/// Singleton driven by <see cref="BackupQueueService"/>'s worker, reaching the scoped DbContext through
/// <see cref="IServiceScopeFactory"/> (ADR-0004) — the same shape as <see cref="BackupService"/>, whose
/// event shell, spool-then-upload ordering and audit vocabulary it deliberately mirrors.
/// </para>
/// <para>
/// Not sealed, and <see cref="RunAsync"/> virtual, for the reason <see cref="BackupQueueService"/>'s
/// enqueues are: the bundle export composes this service, and a test of what a bundle <em>contains</em>
/// should not need a Docker daemon and a live PostgreSQL to produce the one archive it wraps.
/// </para>
/// </remarks>
public class InstanceBackupService(
    BackupArchiveService archiveService,
    PostgresDumpService postgres,
    SelfPostgresLocator locator,
    BackupStorageFactory storageFactory,
    BackupRetentionRunner retention,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    AuditLog audit,
    ILogger<InstanceBackupService> logger) {
    /// <summary>How the instance backup names itself in the audit trail and the run log.</summary>
    internal const string AuditTarget = "watchtower (instance)";

    /// <summary>
    /// The <c>formatVersion</c> of an instance manifest. Its own sequence, independent of the stack
    /// manifest's: the two describe different things and will not move together.
    /// </summary>
    internal const int ManifestFormatVersion = 1;

    /// <summary>The <c>kind</c> an instance manifest declares, so a reader can tell the two apart.</summary>
    internal const string ManifestKind = "watchtower-instance";

    /// <summary>Runs the self-backup for an event created by <see cref="BackupQueueService.EnqueueInstance"/>.</summary>
    /// <param name="backupEventId">The stackless event tracking this run.</param>
    /// <param name="ct">The worker's token.</param>
    public async Task ExecuteInstanceBackupAsync(int backupEventId, CancellationToken ct) {
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
            var result = await RunAsync(backup, Log, ct);
            await FinishAsync(backupEventId, success: true, output.ToString(), result.RelativePath, result.SizeBytes);
            await audit.RecordAsync(BackupService.AuditCategory, "run", AuditTarget,
                $"{Summary(triggeredBy, backup)} · {result.Databases.Count} database(s)"
                + $", {result.SizeBytes} bytes → {result.RelativePath}",
                ct: CancellationToken.None);
            logger.LogInformation(
                "Instance backup completed: {RemotePath} ({SizeBytes} bytes)", result.RelativePath, result.SizeBytes);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            Log($"FAILED: {ex.Message}");
            await FinishAsync(backupEventId, success: false, output.ToString(), remotePath: null, sizeBytes: null);
            await audit.RecordAsync(BackupService.AuditCategory, "run", AuditTarget, Summary(triggeredBy, backup),
                success: false, error: ex.Message, ct: CancellationToken.None);
            logger.LogWarning(ex, "Instance backup failed");
        }
    }

    /// <summary>
    /// Takes the dump, builds the archive, uploads it and prunes — the whole run, without the event
    /// bookkeeping. Also the bundle export's first step (ADR-0027 stage 2), which needs the archive but
    /// writes its own event.
    /// </summary>
    /// <param name="backup">The options the run operates under.</param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <param name="ct">The run's token.</param>
    /// <exception cref="InvalidOperationException">No passphrase, or the database is not reachable as a container.</exception>
    public virtual async Task<InstanceArchiveResult> RunAsync(
        BackupOptions backup, Action<string> log, CancellationToken ct) {
        if (string.IsNullOrEmpty(backup.EncryptionPassphrase))
            throw new InvalidOperationException(
                "Backing up Watchtower itself needs an encryption passphrase: the dump carries every "
                + "database role's password hash, the data-protection key ring and every certificate's "
                + "private key. Set one under Settings → Backups first.");

        var takenAt = DateTimeOffset.UtcNow;
        var target = await locator.LocateAsync(log, ct);
        var dumpTarget = target.ToDumpTarget();

        // Proven before anything is written: a database we cannot reach has to fail the run here.
        var connection = await postgres.PreflightAsync(dumpTarget, log, ct);

        var instance = backup.ResolveInstanceName();
        var directory = BackupNaming.InstanceDirectory(instance);
        var fileName = BackupNaming.FileName(BackupNaming.InstanceFileStem, takenAt, encrypted: true);
        var relativePath = $"{directory}/{fileName}";

        var spoolPath = Path.Combine(Path.GetTempPath(), $"watchtower-{fileName}.spool");
        var dumpSpool = Path.Combine(Path.GetTempPath(), $"watchtower-dump-{Guid.NewGuid():N}.sql");
        try {
            var dumped = await postgres.DumpAsync(dumpTarget, connection, dumpSpool, log, ct);
            var file = $"{PostgresDumpService.DumpDirectory}/{SelfPostgresLocator.ServiceName}.sql";
            var dump = new BackupService.BackupDumpEntry(
                SelfPostgresLocator.ServiceName, DumpEngine.Postgres, file, target.Image, connection.User,
                target.ContainerName, Volumes: [], dumped.Databases, dumped.SizeBytes);

            var manifest = await BuildManifestAsync(instance, target, takenAt, dump, ct);
            await using (var spool = File.Create(spoolPath)) {
                var sink = BackupEncryption.CreateEncryptingStream(spool, backup.EncryptionPassphrase!);
                try {
                    await using (var gzip = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true))
                        // No volumes: the archive is the dump and the manifest. WriteArchiveAsync writes
                        // the `backup/` directory entry itself, which is what makes that legal.
                        await archiveService.WriteArchiveAsync(
                            [], manifest, [new BackupExtraFile(file, dumpSpool)], gzip, backup.HelperImage, ct);
                } finally {
                    await sink.DisposeAsync(); // flushes the final cipher block
                }
            }

            var sizeBytes = new FileInfo(spoolPath).Length;
            log($"Snapshot complete: {sizeBytes} bytes (encrypted)");

            using var storage = storageFactory.Create(backup);
            log($"Uploading to {storage.Description}: {relativePath}");
            await storage.UploadAsync(relativePath, async (dest, uploadCt) => {
                await using var read = File.OpenRead(spoolPath);
                await read.CopyToAsync(dest, uploadCt);
            }, ct);

            await retention.ApplyAsync(storage, directory, backup, log, ct);
            return new InstanceArchiveResult(
                relativePath, fileName, directory, sizeBytes, takenAt, dumped.Databases);
        } finally {
            foreach (var path in new[] { dumpSpool, spoolPath }) {
                try {
                    if (File.Exists(path)) File.Delete(path);
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to delete instance backup spool file {SpoolPath}", path);
                }
            }
        }
    }

    /// <summary>
    /// The instance archive's self-description, written to <c>backup/backup-manifest.json</c> — the same
    /// path a stack archive uses, with <c>kind</c> telling a reader which of the two it is holding.
    /// </summary>
    /// <remarks>
    /// <c>appVersion</c> and <c>lastMigrationId</c> are what make the archive restorable rather than
    /// merely readable: schema migrations only roll forward, so a restore has to be able to refuse a dump
    /// this binary has never known a schema for (<see cref="InstanceVersion"/>).
    /// </remarks>
    internal async Task<string> BuildManifestAsync(
        string instance, SelfPostgresTarget target, DateTimeOffset takenAt,
        BackupService.BackupDumpEntry dump, CancellationToken ct) {
        string? lastMigration;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            lastMigration = await InstanceVersion.LastMigrationAsync(db, ct);
        }
        return BuildManifest(instance, target, takenAt, dump, lastMigration);
    }

    /// <summary>The manifest as a pure function of the run's facts, so its shape is testable on its own.</summary>
    internal static string BuildManifest(
        string instance, SelfPostgresTarget target, DateTimeOffset takenAt,
        BackupService.BackupDumpEntry dump, string? lastMigrationId) {
        var manifest = new JsonObject {
            ["formatVersion"] = ManifestFormatVersion,
            ["kind"] = ManifestKind,
            ["tool"] = "watchtower",
            ["instance"] = instance,
            ["appVersion"] = InstanceVersion.App,
            ["lastMigrationId"] = lastMigrationId,
            ["database"] = target.Database,
            ["createdAtUtc"] = takenAt.UtcDateTime.ToString("O"),
            ["encrypted"] = true,
            ["dumps"] = new JsonArray(BackupService.DumpNode(dump)),
        };
        return manifest.ToJsonString();
    }

    /// <summary>The effective settings a run operated under, for its audit row. Never includes secrets.</summary>
    internal static string Summary(string trigger, BackupOptions backup) {
        var provider = backup.ResolveProvider() == BackupProviderKind.Local ? "local" : "sftp";
        return $"{trigger} · {provider} · encrypted · {BackupService.RetentionSummary(backup)}";
    }

    private async Task FinishAsync(
        int backupEventId, bool success, string output, string? remotePath, long? sizeBytes) {
        // Not the caller's token: the terminal state must be written even when the run was cancelled.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var evt = await db.BackupEvents.FirstOrDefaultAsync(e => e.Id == backupEventId, CancellationToken.None);
        if (evt is null) return;
        evt.Status = success ? BackupStatuses.Success : BackupStatuses.Failed;
        // PostgreSQL text cannot hold NUL (22021), and the log may carry one — exec stderr is raw
        // process output. An unsaveable outcome would leave the event stuck as "running" forever.
        evt.Output = output.Replace("\0", "");
        evt.RemotePath = remotePath;
        evt.SizeBytes = sizeBytes;
        evt.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
