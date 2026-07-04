using Microsoft.EntityFrameworkCore;
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
        var exists = await db.Stacks.AnyAsync(s => s.Id == command.Id, ct);
        if (!exists)
            return AppError.NotFound($"Stack {command.Id} not found");

        var result = deployQueue.Enqueue(command.Id, "manual");
        return new Response(new DeployAcceptedDto(result.DeployEventId, result.Status));
    }
}
