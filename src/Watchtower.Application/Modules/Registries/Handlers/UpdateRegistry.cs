using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Registries.Handlers;

/// <summary>Updates a registry entry.</summary>
[Handler("registries.update")]
public sealed class UpdateRegistry(WatchtowerDbContext db)
    : IHandler<UpdateRegistry.Command, Result<UpdateRegistry.Response>> {
    public sealed record Command(int Id, string Name, string Url, int? CredentialId);
    public sealed record Response(RegistryDto Registry);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var registry = await db.Registries.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (registry is null)
            return AppError.NotFound($"Registry {command.Id} not found");

        registry.Name = command.Name;
        registry.Url = command.Url;
        registry.CredentialId = command.CredentialId;
        await db.SaveChangesAsync(ct);

        var credentialName = command.CredentialId is { } cid
            ? await db.Credentials.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;

        return new Response(new RegistryDto(
            registry.Id, registry.Name, registry.Url, registry.CredentialId, credentialName, registry.CreatedAt));
    }
}
