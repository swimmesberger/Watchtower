using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Realms.Handlers;

/// <summary>
/// Lists every realm with what it currently holds, ordered by slug. The roster an administrator picks
/// from when placing an account, a group or a category.
/// </summary>
[Handler("realms.list")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListRealms(WatchtowerDbContext db)
    : IHandler<ListRealms.Query, Result<ListRealms.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<RealmDto> Realms);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // Correlated subqueries rather than joins-and-groups: one row per realm comes back either way, and
        // this shape keeps an empty realm — the one an administrator is most likely to be about to delete —
        // in the result.
        var realms = await db.Realms.AsNoTracking()
            .OrderBy(r => r.Slug)
            .Select(r => new RealmDto(
                r.Id,
                r.Name,
                r.Slug,
                r.AuthHost,
                r.IsSystem,
                db.Users.Count(u => u.RealmId == r.Id),
                db.Groups.Count(g => g.RealmId == r.Id),
                db.StackTemplates.Count(t => t.RealmId == r.Id),
                r.CreatedAt))
            .ToListAsync(ct);

        return new Response(realms);
    }
}
