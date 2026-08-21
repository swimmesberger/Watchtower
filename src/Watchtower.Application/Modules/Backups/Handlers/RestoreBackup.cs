using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Enqueues a restore of one archive (a name from <c>backups.listRemote</c>) into the stack's
/// volumes — the destructive inverse of a backup: the stack's containers are stopped, the volumes
/// present in the archive are wiped and refilled from it, then the containers restart. Refused
/// while a deploy is running (the containers would be recreated mid-restore) or while the stack
/// already has a backup/restore queued or running.
/// </summary>
[Handler("backups.restore")]
public sealed class RestoreBackup(
    WatchtowerDbContext db,
    BackupQueueService queue,
    IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<RestoreBackup.Command, Result<RestoreBackup.Response>> {
    public sealed record Command(int StackId, string FileName);

    public sealed record Response(BackupRunAcceptedDto Restore);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == command.StackId)
            .Select(s => new { s.LastDeployStatus })
            .FirstOrDefaultAsync(ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        // The name must be exactly one of ours: no separators (it is joined into a remote path) and
        // a parseable backup timestamp — which also rejects anything a traversal could smuggle in.
        var fileName = command.FileName.Trim();
        if (fileName.Contains('/') || fileName.Contains('\\')
            || BackupNaming.ParseTimestamp(fileName) is null)
            return AppError.Validation("FileName must be a backup archive name as listed by backups.listRemote.");

        if (fileName.EndsWith(".enc", StringComparison.Ordinal)
            && string.IsNullOrEmpty(options.CurrentValue.Backup.EncryptionPassphrase))
            return AppError.Validation(
                "This archive is encrypted, but no encryption passphrase is configured. Set it under Settings → Backups first.");

        if (stack.LastDeployStatus is DeployStatus.Running or DeployStatus.Queued)
            return AppError.Conflict("A deploy is in progress — restore once it has finished.");

        var result = queue.TryEnqueueRestore(command.StackId, fileName);
        if (result is null)
            return AppError.Conflict("A backup or restore is already queued or running for this stack.");
        return new Response(new BackupRunAcceptedDto(result.BackupEventId, result.Status));
    }
}
