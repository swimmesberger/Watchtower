using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Prunes a stack's backup history: one event (<see cref="Command.EventId"/>), or every finished one
/// when it is omitted. A queued or running backup or restore is never deleted — its worker still writes
/// to the row and a chained deploy waits on its outcome — so "clear" leaves it in place and deleting it
/// by id is refused.
/// </summary>
/// <remarks>
/// Only the history row goes; the archive stays in the storage. Restores pick from the storage listing
/// (<c>backups.listRemote</c>), never from these rows, and retention prunes archives on its own terms,
/// so an archive whose event was deleted is still restorable until retention removes it.
/// </remarks>
[Handler("backups.deleteEvents")]
public sealed class DeleteBackupEvents(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<DeleteBackupEvents.Command, Result<DeleteBackupEvents.Response>> {
    /// <param name="StackId">The stack whose history is pruned.</param>
    /// <param name="EventId">One event of that stack, or null for every finished event.</param>
    public sealed record Command(int StackId, int? EventId = null);

    /// <param name="Deleted">How many events were removed.</param>
    public sealed record Response(int Deleted);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stackName = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == command.StackId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);
        if (stackName is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        if (command.EventId is { } eventId) {
            var status = await db.BackupEvents.AsNoTracking()
                .Where(e => e.Id == eventId && e.StackId == command.StackId)
                .Select(e => e.Status)
                .FirstOrDefaultAsync(ct);
            if (status is null)
                return AppError.NotFound($"Backup event {eventId} not found on stack {command.StackId}");
            if (status is BackupStatuses.Queued or BackupStatuses.Running)
                return AppError.Conflict($"Backup event {eventId} is still {status} and cannot be deleted.");
        }

        // The status filter is repeated in the delete itself: a run picked up between the check above
        // and this statement stays, rather than losing the row its worker is about to write to.
        var deleted = await db.BackupEvents
            .Where(e => e.StackId == command.StackId
                && (command.EventId == null || e.Id == command.EventId)
                && e.Status != BackupStatuses.Queued && e.Status != BackupStatuses.Running)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            await audit.RecordAsync(BackupService.AuditCategory,
                command.EventId is null ? "stack.events.clear" : "stack.events.delete", stackName,
                command.EventId is null
                    ? $"cleared the backup history ({deleted} event{(deleted == 1 ? "" : "s")}); archives kept"
                    : $"deleted backup event {command.EventId}; archive kept",
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(deleted);
    }
}
