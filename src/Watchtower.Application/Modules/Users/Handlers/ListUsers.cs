using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Lists accounts, ordered by login name, optionally narrowed to one realm. Password hashes and stamps
/// are never projected — see <see cref="UserDto"/>.
/// </summary>
/// <remarks>
/// The default is every realm, not the operator one: this surface is system-realm-only to begin with
/// (<see cref="SystemRealmAuthorizer"/>), so its caller is an instance administrator who is entitled to
/// see the whole estate — and defaulting to a filtered view would hide accounts from the person
/// responsible for them.
/// </remarks>
[Handler("users.list")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListUsers(WatchtowerDbContext db, TimeProvider time)
    : IHandler<ListUsers.Query, Result<ListUsers.Response>> {
    /// <summary>Optional realm filter; omitted means every realm.</summary>
    public sealed record Query(int? RealmId = null);

    public sealed record Response(IReadOnlyList<UserDto> Users);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // Materialize first: the lockout flag is a DateTimeOffset comparison, which EF Core's SQLite
        // provider cannot translate (see UserMapping.ToDto). The table is an operator roster — tens of
        // rows, not a page-worthy dataset.
        var accounts = db.Users.AsNoTracking().AsQueryable();
        if (query.RealmId is { } realmId) accounts = accounts.Where(u => u.RealmId == realmId);

        var users = await accounts
            .OrderBy(u => u.UserName)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        return new Response([.. users.Select(u => UserMapping.ToDto(u, now))]);
    }
}
