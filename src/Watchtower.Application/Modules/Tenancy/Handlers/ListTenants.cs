using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Lists the tenants (instance stacks) of a template with their domain and last-deploy status.</summary>
[Handler("templates.listTenants")]
public sealed class ListTenants(WatchtowerDbContext db)
    : IHandler<ListTenants.Query, Result<ListTenants.Response>> {
    public sealed record Query(int TemplateId);
    public sealed record Response(IReadOnlyList<TenantDto> Tenants);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stacks = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == query.TemplateId)
            .OrderBy(s => s.TenantSlug)
            .ToListAsync(ct);

        var stackIds = stacks.Select(s => s.Id).ToList();
        var domains = await db.Routes.AsNoTracking()
            .Where(r => stackIds.Contains(r.StackId) && r.IsPrimary)
            .Select(r => new { r.StackId, r.Domain })
            .ToListAsync(ct);
        var domainByStack = domains
            .GroupBy(x => x.StackId)
            .ToDictionary(g => g.Key, g => g.First().Domain);

        var tenants = stacks.Select(s => new TenantDto(
            s.Id,
            s.TenantSlug ?? "",
            s.Name,
            domainByStack.GetValueOrDefault(s.Id),
            s.LastDeployStatus?.ToString().ToLowerInvariant(),
            s.LastDeployedAt)).ToList();
        return new Response(tenants);
    }
}
