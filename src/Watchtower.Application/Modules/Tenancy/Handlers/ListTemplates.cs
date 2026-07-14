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
            .OrderBy(t => t.Name)
            .Select(t => new { Template = t, Count = t.Instances.Count })
            .ToListAsync(ct);
        return new Response(rows.Select(x => TenancyMapping.ToDto(x.Template, x.Count)).ToList());
    }
}
