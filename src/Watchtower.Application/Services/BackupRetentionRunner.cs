using Microsoft.Extensions.Logging;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Prunes one remote directory to the configured retention after a successful run (ADR-0016 §4), and
/// records what it deleted. Shared by the stack runs and the instance self-backup (ADR-0027), so both
/// prune by the same rule and leave the same trail.
/// </summary>
/// <remarks>
/// Never allowed to fail the run that called it: the archive is already on the storage by this point,
/// so an unreachable prune is a warning on the run and a failed audit row, and the next successful run
/// tries again. <see cref="BackupRetention.SelectDeletions"/> decides — it only ever considers names
/// that parse as Watchtower archives, and never the newest one.
/// </remarks>
public sealed class BackupRetentionRunner(AuditLog audit, ILogger<BackupRetentionRunner> logger) {
    /// <summary>How many deleted names one audit row lists before it is elided.</summary>
    private const int AuditListLimit = 10;

    /// <summary>Applies the retention policy to <paramref name="directory"/>.</summary>
    /// <param name="storage">The storage the run uploaded to, already open.</param>
    /// <param name="directory">The provider-relative directory to prune.</param>
    /// <param name="backup">The options the run operated under.</param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <param name="ct">The run's token.</param>
    public async Task ApplyAsync(
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
                var listed = string.Join(", ", deletions.Take(AuditListLimit));
                await audit.RecordAsync(BackupService.AuditCategory, "retention.prune", directory,
                    $"{BackupService.RetentionSummary(backup)} · deleted {deletions.Count} archive(s): {listed}"
                    + (deletions.Count > AuditListLimit ? ", …" : ""),
                    ct: CancellationToken.None);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            // The backup itself succeeded — an unreachable prune retries on the next run.
            log($"WARNING: retention pruning failed: {ex.Message}");
            await audit.RecordAsync(BackupService.AuditCategory, "retention.prune", directory,
                BackupService.RetentionSummary(backup), success: false, error: ex.Message,
                ct: CancellationToken.None);
            logger.LogWarning(ex, "Retention pruning failed for {Directory}", directory);
        }
    }
}
