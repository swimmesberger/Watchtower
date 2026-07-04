using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Registries.Handlers;

/// <summary>Lists all registries (with their linked credential name) ordered by name.</summary>
[Handler("registries.list")]
public sealed class ListRegistries(WatchtowerDbContext db)
    : IHandler<ListRegistries.Query, Result<ListRegistries.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<RegistryDto> Registries);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var items = await db.Registries.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RegistryDto(
                r.Id, r.Name, r.Url, r.CredentialId,
                r.Credential != null ? r.Credential.Name : null, r.CreatedAt))
            .ToListAsync(ct);
        return new Response(items);
    }
}
