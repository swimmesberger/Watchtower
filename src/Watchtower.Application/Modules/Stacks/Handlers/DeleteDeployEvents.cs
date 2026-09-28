using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Prunes a stack's deploy history: one event (<see cref="Command.EventId"/>), or every finished one
/// when it is omitted. A queued or running deploy is never deleted — the deploy queue still writes to
/// its row, and the live log streams from it — so "clear" leaves it in place and deleting it by id is
/// refused.
/// </summary>
/// <remarks>
/// Only the history row goes. Nothing a deploy produced depends on it: the stack's own
/// <c>LastDeployStatus</c> is denormalised onto the stack, and a release a deleted event pointed at simply
/// becomes eligible for pruning like any other release no deploy references.
/// </remarks>
[Handler("stacks.deleteEvents")]
public sealed class DeleteDeployEvents(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<DeleteDeployEvents.Command, Result<DeleteDeployEvents.Response>> {
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
            var status = await db.DeployEvents.AsNoTracking()
                .Where(e => e.Id == eventId && e.StackId == command.StackId)
                .Select(e => e.Status)
                .FirstOrDefaultAsync(ct);
            if (status is null)
                return AppError.NotFound($"Deploy event {eventId} not found on stack {command.StackId}");
            if (status is "queued" or "running")
                return AppError.Conflict($"Deploy event {eventId} is still {status} and cannot be deleted.");
        }

        // The status filter is repeated in the delete itself: a deploy picked up between the check above
        // and this statement stays, rather than losing the row its worker is about to write to.
        var deleted = await db.DeployEvents
            .Where(e => e.StackId == command.StackId
                && (command.EventId == null || e.Id == command.EventId)
                && e.Status != "queued" && e.Status != "running")
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            await audit.RecordAsync(StackLifecycle.AuditCategory,
                command.EventId is null ? "stack.events.clear" : "stack.events.delete", stackName,
                command.EventId is null
                    ? $"cleared the deploy history ({deleted} event{(deleted == 1 ? "" : "s")})"
                    : $"deleted deploy event {command.EventId}",
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(deleted);
    }
}
