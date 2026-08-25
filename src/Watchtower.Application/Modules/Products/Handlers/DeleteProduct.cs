using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Removes a product nothing deploys. Deliberately the only thing it can remove: the foreign keys are
/// <c>Restrict</c> on both sides, so a product still referenced by a stack or a template is refused
/// rather than cascaded. Deleting a product that took every stack of it with it would be a blast radius
/// discovered afterwards. The refusal <em>message</em> follows <c>realms.delete</c> — count the
/// blockers, name them, say what to do first — though nothing else about the two is alike: realm
/// administration is <c>[RequireRole("Admin")]</c>, while products are ungated like the rest of the
/// deployment surface (see <see cref="ProductsModule"/>).
/// </summary>
[Handler("products.delete")]
public sealed class DeleteProduct(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<DeleteProduct.Command, Result<DeleteProduct.Response>> {
    /// <summary>How many blockers the refusal names before it stops listing and counts the rest.</summary>
    private const int MaxNamed = 5;

    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == command.Id, ct);
        if (product is null)
            return AppError.NotFound($"Product {command.Id} not found.");

        var stacks = await db.Stacks.AsNoTracking()
            .Where(s => s.ProductId == product.Id).OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct);
        var templates = await db.StackTemplates.AsNoTracking()
            .Where(t => t.ProductId == product.Id).OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);

        if (stacks.Count + templates.Count > 0) {
            var parts = new List<string>(2);
            if (stacks.Count > 0) parts.Add($"{stacks.Count} stack(s) ({Name(stacks)})");
            if (templates.Count > 0) parts.Add($"{templates.Count} template(s) ({Name(templates)})");
            return AppError.Conflict(
                $"Product '{product.Name}' is still deployed by {string.Join(" and ", parts)}. "
                + "Delete them, or point them at another product, first.");
        }

        var name = product.Name;
        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);

        // Past the commit point, delete-then-audit like the realms and users handlers: a delete that
        // fails must not leave a trail claiming it happened.
        await audit.RecordAsync(
            ProductMapping.AuditCategory, "product.delete", name,
            $"{product.RepositoryUrl} ({product.ComposeFilePath})",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(command.Id);
    }

    private static string Name(List<string> names) => names.Count <= MaxNamed
        ? string.Join(", ", names)
        : string.Join(", ", names.Take(MaxNamed)) + $", and {names.Count - MaxNamed} more";
}
