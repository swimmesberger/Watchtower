using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Audit.Handlers;

/// <summary>
/// The distinct categories, actions and actors the trail actually contains, each sorted — what the
/// Audit page's filter dropdowns offer.
/// </summary>
/// <remarks>
/// Read off the rows rather than off a vocabulary: a fresh instance would otherwise present dropdowns
/// of values that all yield nothing, and a category or action a future writer introduces becomes
/// filterable without a frontend edit. Rows with no actor are offered as <see cref="ListAuditEvents.SystemActor"/>.
/// </remarks>
[Handler("audit.listFacets")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListAuditFacets(WatchtowerDbContext db)
    : IHandler<ListAuditFacets.Query, Result<ListAuditFacets.Response>> {
    public sealed record Query;

    public sealed record Response(
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> Actions,
        IReadOnlyList<string> Actors);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var rows = db.AuditEvents.AsNoTracking();
        var categories = await rows.Select(e => e.Category).Distinct().OrderBy(c => c).ToListAsync(ct);
        var actions = await rows.Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
        var actors = await rows.Select(e => e.Actor).Distinct().ToListAsync(ct);
        var named = actors
            .Select(a => a ?? ListAuditEvents.SystemActor)
            .Distinct()
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
        return new Response(categories, actions, named);
    }
}
