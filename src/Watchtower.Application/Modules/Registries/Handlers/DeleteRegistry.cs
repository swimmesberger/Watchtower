using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Registries.Handlers;

/// <summary>Deletes a registry entry.</summary>
[Handler("registries.delete")]
public sealed class DeleteRegistry(WatchtowerDbContext db)
    : IHandler<DeleteRegistry.Command, Result<DeleteRegistry.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var deleted = await db.Registries.Where(r => r.Id == command.Id).ExecuteDeleteAsync(ct);
        return deleted == 0
            ? AppError.NotFound($"Registry {command.Id} not found")
            : new Response(command.Id);
    }
}
