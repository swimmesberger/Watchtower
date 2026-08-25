using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// A product with the stacks and templates that reference it — the roster an operator needs before
/// editing a shared source, and the same list <c>products.delete</c> refuses with.
/// </summary>
[Handler("products.get")]
public sealed class GetProduct(WatchtowerDbContext db)
    : IHandler<GetProduct.Query, Result<GetProduct.Response>> {
    public sealed record Query(int Id) : IQuery;

    public sealed record Response(
        ProductDto Product,
        IReadOnlyList<ProductStackDto> Stacks,
        IReadOnlyList<ProductTemplateDto> Templates);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var product = await db.Products.AsNoTracking()
            .Include(p => p.Credential)
            .FirstOrDefaultAsync(p => p.Id == query.Id, ct);
        if (product is null)
            return AppError.NotFound($"Product {query.Id} not found.");

        // Projected rather than materialized as entities: the effective branch is the only resolved
        // value either roster needs, and it is a two-level coalesce the database can do.
        var rows = await db.Stacks.AsNoTracking()
            .Where(s => s.ProductId == product.Id)
            .OrderBy(s => s.Name)
            .Select(s => new {
                s.Id,
                s.Name,
                // The two-level coalesce ProductSourceResolver applies, done in SQL.
                Branch = s.BranchOverride
                    ?? (s.Template != null ? s.Template.BranchOverride : null)
                    ?? product.DefaultBranch,
                s.BranchOverride,
                s.TemplateId,
                s.TenantSlug,
                s.LastDeployStatus,
                s.LastDeployedAt,
            })
            .ToListAsync(ct);
        // The wire form of the status is lowercase; the enum-to-string conversion is a client-side
        // detail rather than something to ask the database to reproduce.
        var stacks = rows
            .Select(r => new ProductStackDto(
                r.Id, r.Name, r.Branch, r.BranchOverride, r.TemplateId, r.TenantSlug,
                r.LastDeployStatus?.ToString().ToLowerInvariant(), r.LastDeployedAt))
            .ToList();

        var templates = await db.StackTemplates.AsNoTracking()
            .Where(t => t.ProductId == product.Id)
            .OrderBy(t => t.Name)
            .Select(t => new ProductTemplateDto(
                t.Id, t.Name, t.BranchOverride ?? product.DefaultBranch, t.BranchOverride, t.Instances.Count))
            .ToListAsync(ct);

        var dto = ProductMapping.ToDto(product, product.Credential?.Name, stacks.Count, templates.Count);
        return new Response(dto, stacks, templates);
    }
}
