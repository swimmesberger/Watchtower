using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Registries.Handlers;

/// <summary>
/// Lists all registries (with their linked credential name) ordered by name, plus the read-only
/// entries found in the host docker config — the same base layer deploys and CI sync already use.
/// </summary>
[Handler("registries.list")]
public sealed class ListRegistries(WatchtowerDbContext db, RegistryAuthBuilder registryAuth)
    : IHandler<ListRegistries.Query, Result<ListRegistries.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<RegistryDto> Registries, IReadOnlyList<HostRegistryDto> HostRegistries);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var items = await db.Registries.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RegistryDto(
                r.Id, r.Name, r.Url, r.CredentialId,
                r.Credential != null ? r.Credential.Name : null, r.CreatedAt))
            .ToListAsync(ct);
        // Host entries only — passwords stay server-side (ResolvedRegistry never crosses the wire).
        var host = registryAuth.ListResolvedRegistries()
            .Where(r => r.FromHostConfig)
            .Select(r => new HostRegistryDto(r.Url, r.Username))
            .ToList();
        return new Response(items, host);
    }
}
