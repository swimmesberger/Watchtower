using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>Lists every product with how many stacks and templates deploy it, ordered by name.</summary>
[Handler("products.list")]
public sealed class ListProducts(WatchtowerDbContext db)
    : IHandler<ListProducts.Query, Result<ListProducts.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<ProductDto> Products);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // One query with correlated counts rather than three round trips plus client-side grouping:
        // the catalogue page renders both counts on every row. The latest release rides along the same
        // way — newest is the highest id (ADR-0026), never the newest timestamp.
        var rows = await db.Products.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new {
                Product = p,
                CredentialName = p.Credential != null ? p.Credential.Name : null,
                StackCount = p.Stacks.Count,
                TemplateCount = p.Templates.Count,
                Latest = p.Releases
                    .OrderByDescending(r => r.Id)
                    .Select(r => new ProductReleaseSummaryDto(r.Id, r.Version, r.CreatedAt))
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return new Response(rows
            .Select(r => ProductMapping.ToDto(
                r.Product, r.CredentialName, r.StackCount, r.TemplateCount, r.Latest))
            .ToList());
    }
}
