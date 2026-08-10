using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Lists the stacks allowed to manage a template's tenants through the public management API
/// (<c>/api/mgmt/*</c>).
/// </summary>
/// <remarks>
/// <c>[RequireRole("Admin")]</c> like the rest of the grant surface: these rows are the entire
/// authorization behind that API, so reading them is privilege information, not tenant data.
/// </remarks>
[Handler("templates.listGrants")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListGrants(WatchtowerDbContext db)
    : IHandler<ListGrants.Query, Result<ListGrants.Response>> {
    /// <summary>Which template's grants to list.</summary>
    /// <param name="TemplateId">Template whose grants are returned.</param>
    public sealed record Query(int TemplateId);

    /// <summary>The template's grants, ordered by stack id.</summary>
    /// <param name="Grants">One entry per stack that may manage this template's tenants.</param>
    public sealed record Response(IReadOnlyList<TemplateGrantDto> Grants);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (!await db.StackTemplates.AnyAsync(t => t.Id == query.TemplateId, ct))
            return AppError.NotFound($"Template {query.TemplateId} not found");

        var grants = await db.TemplateManagementGrants.AsNoTracking()
            .Where(g => g.TemplateId == query.TemplateId)
            .OrderBy(g => g.StackId)
            .Select(g => new TemplateGrantDto(
                g.StackId, g.Stack!.Name, g.AllowDelete, g.CreatedAt))
            .ToListAsync(ct);

        return new Response(grants);
    }
}
