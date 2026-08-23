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
        var accounts = db.Users.AsNoTracking().AsQueryable();
        if (query.RealmId is { } realmId) accounts = accounts.Where(u => u.RealmId == realmId);

        var now = time.GetUtcNow();
        var users = await accounts
            .OrderBy(u => u.UserName)
            .Select(u => new UserDto(
                u.Id, u.UserName, u.Email, u.IsAdmin, u.Disabled,
                u.LockoutEnd != null && u.LockoutEnd > now,
                u.TwoFactorEnabled, u.RealmId, u.CreatedAt))
            .ToListAsync(ct);
        return new Response(users);
    }
}
