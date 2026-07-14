using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Fetches a template with its base environment variables.</summary>
[Handler("templates.get")]
public sealed class GetTemplate(WatchtowerDbContext db)
    : IHandler<GetTemplate.Query, Result<GetTemplate.Response>> {
    public sealed record Query(int Id);
    public sealed record Response(StackTemplateDto Template, IReadOnlyList<TemplateEnvVarDto> BaseEnvVars);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var template = await db.StackTemplates.AsNoTracking()
            .Include(t => t.BaseEnvVars)
            .FirstOrDefaultAsync(t => t.Id == query.Id, ct);
        if (template is null)
            return AppError.NotFound($"Template {query.Id} not found");

        var count = await db.Stacks.CountAsync(s => s.TemplateId == query.Id, ct);
        var env = template.BaseEnvVars
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => new TemplateEnvVarDto(v.Id, v.Key, v.Value))
            .ToList();
        return new Response(TenancyMapping.ToDto(template, count), env);
    }
}
