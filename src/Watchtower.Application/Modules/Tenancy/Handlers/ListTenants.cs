using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Lists the tenants (instance stacks) of a template with their domain, last-deploy status and the
/// version each one runs — the Instances roster of design.md §"Product detail page".
/// </summary>
/// <remarks>
/// The pin and the deployed release are <c>Include</c>d rather than looked up per row: the roster is the
/// one screen that answers "which tenant runs which version", and a per-tenant query for a chip is how a
/// 200-tenant fleet turns one screen into two hundred requests. Nothing here computes "behind" — that
/// needs the product's release list, which the page already fetches for the roll-out dialog, and
/// deriving it in two places is how the two would come to disagree.
/// </remarks>
[Handler("templates.listTenants")]
public sealed class ListTenants(WatchtowerDbContext db)
    : IHandler<ListTenants.Query, Result<ListTenants.Response>> {
    public sealed record Query(int TemplateId);
    public sealed record Response(IReadOnlyList<TenantDto> Tenants);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stacks = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == query.TemplateId)
            .OrderBy(s => s.TenantSlug)
            .Include(s => s.PinnedRelease)
            .Include(s => s.LastDeployedRelease)
            .ToListAsync(ct);

        var stackIds = stacks.Select(s => s.Id).ToList();
        var domains = await db.Routes.AsNoTracking()
            .Where(r => r.StackId != null && stackIds.Contains(r.StackId.Value) && r.IsPrimary)
            .Select(r => new { StackId = r.StackId!.Value, r.Domain })
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
            s.LastDeployedAt,
            s.PinnedReleaseId is null ? TenancyMapping.TrackingLatest : TenancyMapping.TrackingPinned,
            TenancyMapping.ReleaseRef(s.PinnedRelease),
            TenancyMapping.ReleaseRef(s.LastDeployedRelease))).ToList();
        return new Response(tenants);
    }
}
