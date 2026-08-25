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
        // the catalogue page renders both counts on every row.
        var rows = await db.Products.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new {
                Product = p,
                CredentialName = p.Credential != null ? p.Credential.Name : null,
                StackCount = p.Stacks.Count,
                TemplateCount = p.Templates.Count,
            })
            .ToListAsync(ct);

        return new Response(rows
            .Select(r => ProductMapping.ToDto(r.Product, r.CredentialName, r.StackCount, r.TemplateCount))
            .ToList());
    }
}
