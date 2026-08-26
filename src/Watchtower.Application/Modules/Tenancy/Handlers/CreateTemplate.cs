using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Creates a stack template. Base environment variables (if any) are set atomically.</summary>
/// <remarks>
/// Like <c>stacks.create</c>, the inline repository fields find-or-create the product (ADR-0026), so an
/// existing client keeps working; a caller that already has one passes <c>ProductId</c> instead.
/// </remarks>
[Handler("templates.create")]
public sealed class CreateTemplate(
    WatchtowerDbContext db, ProductCatalog products, AuditLog audit, ICurrentUser currentUser)
    : IHandler<CreateTemplate.Command, Result<CreateTemplate.Response>> {
    /// <summary>
    /// <paramref name="RealmId"/> and <paramref name="ProductId"/> are optional and last (a default
    /// value is what marks a parameter non-required in the generated schema): a client that predates
    /// realms omits the first and creates an operator-realm category, exactly as it always did, and a
    /// client that predates products omits the second and gets one from its repository fields.
    /// </summary>
    public sealed record Command(
        string Name,
        string RepositoryUrl,
        string ComposeFilePath,
        string Branch,
        int? CredentialId,
        string DomainPattern,
        string TargetServiceName,
        int TargetPort,
        IReadOnlyList<TemplateEnvVarInput>? BaseEnvVars,
        int? RealmId = null,
        int? ProductId = null);

    public sealed record Response(StackTemplateDto Template);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(command.Name))
            return AppError.Validation("Template name is required.");
        if (!command.DomainPattern.Contains("{tenant}"))
            return AppError.Validation("Domain pattern must contain the {tenant} placeholder.");
        if (command.TargetPort is < 1 or > 65535)
            return AppError.Validation("Target port must be between 1 and 65535.");
        if (command.BaseEnvVars is { Count: > 0 } && TenancyMapping.FirstDuplicateKey(command.BaseEnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");
        if (await db.StackTemplates.AnyAsync(t => t.Name == command.Name, ct))
            return AppError.Validation($"A template named '{command.Name}' already exists.");

        // The realm every tenant route created from this template will inherit (design.md §13), and
        // therefore which population may enter them.
        // Loaded rather than merely checked for existence: the response names the realm, and a template
        // built with only the id would project "no realm" until something re-read it.
        var realm = await db.Realms.FirstOrDefaultAsync(
            r => r.Id == (command.RealmId ?? Realm.SystemRealmId), ct);
        if (realm is null)
            return AppError.Validation($"No realm exists with id {command.RealmId ?? Realm.SystemRealmId}.");

        // Opened before the product is resolved so one created implicitly for this template rolls back
        // with it.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (product, created, productError) = await products.ResolveAsync(
            command.ProductId, command.RepositoryUrl, command.ComposeFilePath, command.Branch,
            command.CredentialId, ct);
        if (productError is { } error) return error;

        var template = new StackTemplate {
            RealmId = realm.Id,
            Realm = realm,
            Name = command.Name,
            ProductId = product!.Id,
            Product = product,
            BranchOverride = ProductSourceResolver.OverrideFor(command.Branch, product.DefaultBranch),
            DomainPattern = command.DomainPattern.Trim(),
            TargetServiceName = command.TargetServiceName.Trim(),
            TargetPort = command.TargetPort,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.StackTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        if (command.BaseEnvVars is { Count: > 0 }) {
            foreach (var v in command.BaseEnvVars)
                db.StackTemplateEnvVars.Add(new StackTemplateEnvVar { TemplateId = template.Id, Key = v.Key, Value = v.Value });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        if (created) {
            await audit.RecordAsync(
                ProductCatalog.AuditCategory, "product.create", product.Name,
                $"{product.RepositoryUrl} ({product.ComposeFilePath}) @ {product.DefaultBranch} — "
                + "implicit via templates.create",
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        }

        return new Response(TenancyMapping.ToDto(template, instanceCount: 0));
    }
}
