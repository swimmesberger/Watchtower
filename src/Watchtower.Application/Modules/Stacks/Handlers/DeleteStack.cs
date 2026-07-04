using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Deletes a stack (cascades to its deploy events, env vars, and update check).</summary>
[Handler("stacks.delete")]
public sealed class DeleteStack(WatchtowerDbContext db)
    : IHandler<DeleteStack.Command, Result<DeleteStack.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var deleted = await db.Stacks.Where(s => s.Id == command.Id).ExecuteDeleteAsync(ct);
        return deleted == 0
            ? AppError.NotFound($"Stack {command.Id} not found")
            : new Response(command.Id);
    }
}
