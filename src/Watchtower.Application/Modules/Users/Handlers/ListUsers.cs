using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Lists every account, ordered by login name. Password hashes and stamps are never projected —
/// see <see cref="UserDto"/>.
/// </summary>
[Handler("users.list")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListUsers(WatchtowerDbContext db, TimeProvider time)
    : IHandler<ListUsers.Query, Result<ListUsers.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<UserDto> Users);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // Materialize first: the lockout flag is a DateTimeOffset comparison, which EF Core's SQLite
        // provider cannot translate (see UserMapping.ToDto). The table is an operator roster — tens of
        // rows, not a page-worthy dataset.
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.UserName)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        return new Response([.. users.Select(u => UserMapping.ToDto(u, now))]);
    }
}
