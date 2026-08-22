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
public sealed class ListRealms(WatchtowerDbContext db, RealmResolver realms)
    : IHandler<ListRealms.Query, Result<ListRealms.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<RealmDto> Realms);

    /// <summary>One realm's counts, as the database projects them — a row shape, not a DTO.</summary>
    private sealed record RealmCounts(int RealmId, int UserCount, int GroupCount, int TemplateCount);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // Correlated subqueries rather than joins-and-groups, and evaluated on the server: one row per
        // realm comes back either way, and this shape keeps an empty realm — the one an administrator is
        // most likely to be about to delete — in the result. It is a `Select` rather than a
        // `ToDictionaryAsync` with a lambda value, which EF would only be able to satisfy by fetching
        // every realm and running three counts per row from the client.
        var counts = await db.Realms.AsNoTracking()
            .Select(r => new RealmCounts(
                r.Id,
                db.Users.Count(u => u.RealmId == r.Id),
                db.Groups.Count(g => g.RealmId == r.Id),
                db.StackTemplates.Count(t => t.RealmId == r.Id)))
            .ToListAsync(ct);
        var byRealm = counts.ToDictionary(c => c.RealmId);

        // The login host comes through the resolver rather than off the row: it is the login route's
        // domain with the system realm's Auth:Host fallback behind it (ADR-0021), and that reading lives
        // in exactly one place. `ListAsync` includes the route, so merging it costs no query per realm.
        var listed = new List<RealmDto>(counts.Count);
        foreach (var realm in await realms.ListAsync(ct)) {
            var c = byRealm.TryGetValue(realm.Id, out var found) ? found : new RealmCounts(realm.Id, 0, 0, 0);
            listed.Add(RealmMapping.ToDto(
                realm, c.UserCount, c.GroupCount, c.TemplateCount,
                await realms.LoginHostForAsync(realm, ct)));
        }

        return new Response(listed);
    }
}
