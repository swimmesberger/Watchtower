using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Enqueues a backup of Watchtower's own database on the single-flight backup queue (ADR-0027) and
/// returns the tracking event immediately. Works regardless of the schedule master switch and of
/// <c>Backup:IncludeSelf</c> — both govern the schedule, and an explicit run is an operator's decision.
/// </summary>
/// <remarks>
/// Admin-only, unlike the stack runs. The archive it produces carries every database role's password
/// hash, the data-protection key ring and every certificate's private key, so the ability to place one on
/// the backup storage is the ability to walk away with the instance.
/// </remarks>
[Handler("backups.runInstance")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class RunInstanceBackup(BackupQueueService queue, IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<RunInstanceBackup.Command, Result<RunInstanceBackup.Response>> {
    public sealed record Command;

    public sealed record Response(BackupRunAcceptedDto Backup);

    public ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        // Refused here rather than in the run, so the operator gets the reason in the dialog instead of
        // in a failed event's log.
        if (string.IsNullOrEmpty(options.CurrentValue.Backup.EncryptionPassphrase))
            return ValueTask.FromResult<Result<Response>>(AppError.Validation(
                "Backing up Watchtower itself needs an encryption passphrase: the dump carries every "
                + "database role's password hash, the data-protection key ring and every certificate's "
                + "private key. Set one under Settings → Backups first."));

        var result = queue.EnqueueInstance(BackupTriggers.Manual);
        return ValueTask.FromResult<Result<Response>>(
            new Response(new BackupRunAcceptedDto(result.BackupEventId, result.Status)));
    }
}
