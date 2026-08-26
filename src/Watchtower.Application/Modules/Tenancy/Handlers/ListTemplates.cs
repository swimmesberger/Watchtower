using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Lists all stack templates with their tenant counts.</summary>
[Handler("templates.list")]
public sealed class ListTemplates(WatchtowerDbContext db)
    : IHandler<ListTemplates.Query, Result<ListTemplates.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<StackTemplateDto> Templates);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var rows = await db.StackTemplates.AsNoTracking()
            // The source fields are resolved from the product since ADR-0026.
            .Include(t => t.Product)
            // …and the fleet default is a navigation too: without it every row reports "no default"
            // however many tenants the template pins, because ToDto reads the loaded release, not the id.
            .Include(t => t.DefaultPinnedRelease)
            .OrderBy(t => t.Name)
            .Select(t => new { Template = t, Count = t.Instances.Count })
            .ToListAsync(ct);
        return new Response(rows.Select(x => TenancyMapping.ToDto(x.Template, x.Count)).ToList());
    }
}
