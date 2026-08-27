using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Builds a full backup bundle (ADR-0027 §4) — a fresh dump of Watchtower's own database plus the
/// newest archive of every stack, in one tar — and stages it for download at
/// <c>GET /api/instance/bundle</c>. Returns the tracking event immediately; the export runs on the
/// single-flight backup queue, since it dumps and downloads for as long as that takes.
/// </summary>
/// <remarks>
/// Admin-only, and audited on both sides: the bundle carries the key-protection secret, the backup
/// passphrase and the storage credentials in plain text, so producing one is producing a portable copy
/// of the instance.
/// </remarks>
[Handler("backups.exportBundle")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ExportBackupBundle(
    BackupQueueService queue,
    IOptionsMonitor<WatchtowerOptions> options,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<ExportBackupBundle.Command, Result<ExportBackupBundle.Response>> {
    public sealed record Command;

    public sealed record Response(BackupRunAcceptedDto Export);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        // Refused here rather than in the run, so the operator sees why in the dialog. Both conditions
        // are about the archives the bundle is made of, not about the bundle itself.
        var backup = options.CurrentValue.Backup;
        if (string.IsNullOrEmpty(backup.EncryptionPassphrase))
            return AppError.Validation(
                "A full backup bundle needs an encryption passphrase: it carries a dump of Watchtower's "
                + "own database, which holds every database role's password hash, the data-protection "
                + "key ring and every certificate's private key. Set one under Settings → Backups first.");

        var result = queue.EnqueueBundleExport(BackupTriggers.BundleExport);
        // Recorded when the export is *asked for* as well as when it finishes: the request is the
        // decision, and a bundle that fails to build is still an attempt to take one off the box.
        await audit.RecordAsync(
            BackupService.AuditCategory, "bundle.request", BackupBundleService.AuditTarget,
            $"export requested (event {result.BackupEventId})",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        return new Response(new BackupRunAcceptedDto(result.BackupEventId, result.Status));
    }
}
