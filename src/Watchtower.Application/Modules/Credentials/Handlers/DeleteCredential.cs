using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Credentials.Handlers;

/// <summary>Deletes a credential. Registries/stacks linked to it have their credential cleared (SET NULL).</summary>
[Handler("credentials.delete")]
public sealed class DeleteCredential(WatchtowerDbContext db)
    : IHandler<DeleteCredential.Command, Result<DeleteCredential.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var deleted = await db.Credentials.Where(c => c.Id == command.Id).ExecuteDeleteAsync(ct);
        return deleted == 0
            ? AppError.NotFound($"Credential {command.Id} not found")
            : new Response(command.Id);
    }
}
