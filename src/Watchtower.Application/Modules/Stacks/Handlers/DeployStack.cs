using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Triggers a stack deployment through the deploy queue (internal UI — no auth). External/CI callers
/// use the webhook endpoint instead. Returns the tracking deploy event immediately.
/// </summary>
[Handler("stacks.deploy")]
public sealed class DeployStack(WatchtowerDbContext db, DeployQueueService deployQueue)
    : IHandler<DeployStack.Command, Result<DeployStack.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(DeployAcceptedDto Deploy);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == command.Id)
            .Select(s => new { s.Name, s.DesiredState })
            .FirstOrDefaultAsync(ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.Id} not found");
        // A stopped stack is deliberately disabled (ADR-0025): a deploy would bring its containers
        // back up, so the intent has to be reversed explicitly first.
        if (stack.DesiredState == StackDesiredState.Stopped)
            return AppError.Conflict($"Stack '{stack.Name}' is stopped — start it before deploying.");

        var result = deployQueue.Enqueue(command.Id, "manual");
        return new Response(new DeployAcceptedDto(result.DeployEventId, result.Status));
    }
}
