using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Creates a product explicitly — the SaaS entry point, where the product exists before anything
/// deploys it. The hobby path never reaches here: <c>stacks.create</c> find-or-creates one behind its
/// own repository fields (ADR-0026's implicit-product contract).
/// </summary>
[Handler("products.create")]
public sealed class CreateProduct(
    WatchtowerDbContext db, ProductCatalog catalog, AuditLog audit, ICurrentUser currentUser)
    : IHandler<CreateProduct.Command, Result<CreateProduct.Response>> {
    public sealed record Command(
        string Name,
        string RepositoryUrl,
        string ComposeFilePath,
        string DefaultBranch,
        string? Description = null,
        int? CredentialId = null);

    public sealed record Response(ProductDto Product);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (ProductMapping.Validate(
                command.Name, command.RepositoryUrl, command.ComposeFilePath, command.DefaultBranch,
                out var name, out var repositoryUrl, out var composeFilePath, out var defaultBranch)
            is { } invalid) {
            return AppError.Validation(invalid);
        }

        if (await db.Products.AnyAsync(p => p.Name == name, ct))
            return AppError.Validation($"A product named '{name}' already exists.");

        // Two products over one normalized source would make find-or-create ambiguous, and it resolves
        // by lowest id — so every later stacks.create would silently join the older one and this
        // product would sit in the catalogue collecting nothing.
        if (await catalog.FindConflictAsync(repositoryUrl, composeFilePath, excludeProductId: null, ct)
            is { } clash) {
            return AppError.Validation(
                $"Product '{clash.Name}' already deploys {clash.ComposeFilePath} from this repository. "
                + "Use it, or point this one at a different compose file.");
        }

        string? credentialName = null;
        if (command.CredentialId is { } credentialId) {
            credentialName = await db.Credentials.AsNoTracking()
                .Where(c => c.Id == credentialId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            if (credentialName is null)
                return AppError.NotFound($"Credential {credentialId} not found.");
        }

        var product = new Product {
            Name = name,
            Description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim(),
            RepositoryUrl = repositoryUrl,
            ComposeFilePath = composeFilePath,
            DefaultBranch = defaultBranch,
            CredentialId = command.CredentialId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(
            ProductMapping.AuditCategory, "product.create", product.Name,
            $"{product.RepositoryUrl} ({product.ComposeFilePath}) @ {product.DefaultBranch}",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(ProductMapping.ToDto(product, credentialName, stackCount: 0, templateCount: 0));
    }
}
