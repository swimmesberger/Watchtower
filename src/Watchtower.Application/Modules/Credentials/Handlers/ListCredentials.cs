using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Credentials.Handlers;

/// <summary>Lists all credentials ordered by name. Tokens are never returned.</summary>
[Handler("credentials.list")]
public sealed class ListCredentials(WatchtowerDbContext db)
    : IHandler<ListCredentials.Query, Result<ListCredentials.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<CredentialDto> Credentials);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var items = await db.Credentials.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CredentialDto(c.Id, c.Name, c.Username, c.CreatedAt))
            .ToListAsync(ct);
        return new Response(items);
    }
}
