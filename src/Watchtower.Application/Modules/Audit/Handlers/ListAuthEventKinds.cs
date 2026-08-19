using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Audit.Handlers;

/// <summary>
/// Lists the distinct <see cref="Entities.AuthEvent.Kind"/> values the trail actually contains, sorted.
/// </summary>
/// <remarks>
/// Read off the rows rather than off <see cref="AuthEventKinds"/> so the filter offers what is there:
/// a fresh instance would otherwise present a dropdown of two dozen kinds that all yield nothing, and a
/// kind added by a future writer would need a matching frontend edit to become filterable. Ordered
/// ascending, which groups the dotted vocabulary by its prefix (<c>group.*</c>, <c>login.*</c>,
/// <c>realm.*</c>, <c>user.*</c>) for free.
/// </remarks>
[Handler("audit.kinds")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListAuthEventKinds(WatchtowerDbContext db)
    : IHandler<ListAuthEventKinds.Query, Result<ListAuthEventKinds.Response>> {
    public sealed record Query;

    public sealed record Response(IReadOnlyList<string> Kinds);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var kinds = await db.AuthEvents.AsNoTracking()
            .Select(e => e.Kind)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync(ct);

        return new Response(kinds);
    }
}
