using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Registries.Handlers;

/// <summary>Creates a new registry entry, optionally linked to an existing credential.</summary>
[Handler("registries.create")]
public sealed class CreateRegistry(WatchtowerDbContext db)
    : IHandler<CreateRegistry.Command, Result<CreateRegistry.Response>> {
    public sealed record Command(string Name, string Url, int? CredentialId);
    public sealed record Response(RegistryDto Registry);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var registry = new Registry {
            Name = command.Name,
            Url = command.Url,
            CredentialId = command.CredentialId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Registries.Add(registry);
        await db.SaveChangesAsync(ct);

        var credentialName = command.CredentialId is { } cid
            ? await db.Credentials.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;

        return new Response(new RegistryDto(
            registry.Id, registry.Name, registry.Url, registry.CredentialId, credentialName, registry.CreatedAt));
    }
}
