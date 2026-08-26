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
            .Include(t => t.Product)
            // The fleet default the roll-out dialog opens on, and the "New tenants are pinned to …"
            // line above the roster.
            .Include(t => t.DefaultPinnedRelease)
            // …and the realm, for the same reason: the DTO names the population this setup serves, and a
            // missing include would report "no realm" over a template that has one.
            .Include(t => t.Realm)
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
