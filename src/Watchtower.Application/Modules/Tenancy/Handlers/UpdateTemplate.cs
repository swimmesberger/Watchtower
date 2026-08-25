using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Updates a template. When BaseEnvVars is provided the base env set is replaced atomically.</summary>
/// <remarks>
/// The realm may only be changed while the template has <em>no</em> tenants (docs/central-auth/design.md
/// §13). Moving a populated category would silently re-point every tenant route at another population: the
/// accounts currently using them would stop being admitted on their next request, and the accounts of the
/// new realm would be let in without anybody having granted them anything. Emptying the category first
/// makes that an explicit act rather than a side effect of a form save.
/// <para>
/// The product moves under the same rule and for the same reason (ADR-0026): repointing a populated
/// category at another product would put every tenant on a different codebase at its next deploy. The
/// repository fields are handled as they are on <c>stacks.update</c> — unchanged values pass, a changed
/// one is refused with a pointer at <c>products.update</c>, and <c>Branch</c> maps onto
/// <see cref="StackTemplate.BranchOverride"/>.
/// </para>
/// </remarks>
[Handler("templates.update")]
public sealed class UpdateTemplate(WatchtowerDbContext db)
    : IHandler<UpdateTemplate.Command, Result<UpdateTemplate.Response>> {
    /// <summary>
    /// <paramref name="RealmId"/> omitted leaves the category where it is, and so does
    /// <paramref name="ProductId"/>.
    /// </summary>
    public sealed record Command(
        int Id,
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
        if (!command.DomainPattern.Contains("{tenant}"))
            return AppError.Validation("Domain pattern must contain the {tenant} placeholder.");
        if (command.TargetPort is < 1 or > 65535)
            return AppError.Validation("Target port must be between 1 and 65535.");
        if (command.BaseEnvVars is { Count: > 0 } && TenancyMapping.FirstDuplicateKey(command.BaseEnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");

        var template = await db.StackTemplates
            .Include(t => t.Product)
            .FirstOrDefaultAsync(t => t.Id == command.Id, ct);
        if (template is null)
            return AppError.NotFound($"Template {command.Id} not found");
        if (await db.StackTemplates.AnyAsync(t => t.Name == command.Name && t.Id != command.Id, ct))
            return AppError.Validation($"A template named '{command.Name}' already exists.");

        var tenantCount = await db.Stacks.CountAsync(s => s.TemplateId == template.Id, ct);

        // Everything below decides; nothing writes. The mutation block comes after the last refusal, so
        // a rejected save cannot leave the tracked entity carrying half of it — a repointed ProductId
        // that a later validation error then abandoned would still be sitting in the change tracker for
        // whatever else this scope saves.
        var product = template.Product!;
        if (command.ProductId is { } newProductId && newProductId != template.ProductId) {
            if (tenantCount > 0) {
                return AppError.Conflict(
                    $"Template '{template.Name}' has {tenantCount} tenant(s), so its product cannot be "
                    + "changed — every one of them would deploy a different codebase. Remove them first.");
            }
            var replacement = await db.Products.FirstOrDefaultAsync(p => p.Id == newProductId, ct);
            if (replacement is null)
                return AppError.NotFound($"Product {newProductId} not found.");
            product = replacement;
        }

        // Against the product that will be in force, not the one on the row: a caller moving the
        // template and posting the new product's repository fields is consistent and must pass, while
        // one posting the old product's is telling us its form is stale.
        var effective = ProductSourceResolver.Resolve(product, template.BranchOverride);
        if (RefuseSourceChange(command, product, effective) is { } sourceError)
            return AppError.Validation(sourceError);

        int? newRealmId = null;
        if (command.RealmId is { } realmId && realmId != template.RealmId) {
            if (!await db.Realms.AnyAsync(r => r.Id == realmId, ct))
                return AppError.Validation($"No realm exists with id {realmId}.");
            if (tenantCount > 0) {
                return AppError.Conflict(
                    $"Template '{template.Name}' has {tenantCount} tenant(s), so its realm cannot be " +
                    "changed. Remove them first.");
            }
            newRealmId = realmId;
        }

        // Validation is done; from here it is all writes.
        template.ProductId = product.Id;
        template.Product = product;
        if (newRealmId is { } accepted) template.RealmId = accepted;
        template.Name = command.Name;
        // The template's own base really is the product default — unlike a stack's, which may inherit
        // this very override (see ProductSourceResolver.InheritedBranch).
        template.BranchOverride = ProductSourceResolver.OverrideFor(command.Branch, product.DefaultBranch);
        template.DomainPattern = command.DomainPattern.Trim();
        template.TargetServiceName = command.TargetServiceName.Trim();
        template.TargetPort = command.TargetPort;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (command.BaseEnvVars is not null) {
            await db.StackTemplateEnvVars.Where(v => v.TemplateId == template.Id).ExecuteDeleteAsync(ct);
            foreach (var v in command.BaseEnvVars)
                db.StackTemplateEnvVars.Add(new StackTemplateEnvVar { TemplateId = template.Id, Key = v.Key, Value = v.Value });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Read before the write above, not after: nothing here changes the tenant count, and the value was
        // already needed to decide whether the realm may move.
        return new Response(TenancyMapping.ToDto(template, tenantCount));
    }

    /// <summary>
    /// The message refusing a repository field the caller actually changed, or null. Same contract as
    /// <c>stacks.update</c>: unchanged values pass, because the UI posts the whole object back.
    /// </summary>
    private static string? RefuseSourceChange(Command command, Product product, ProductSource effective) {
        if (Changed(command.RepositoryUrl, effective.RepositoryUrl))
            return Refusal("repository URL", product);
        if (Changed(command.ComposeFilePath, effective.ComposeFilePath))
            return Refusal("compose file path", product);
        if (command.CredentialId is { } credentialId && credentialId != effective.CredentialId)
            return Refusal("git credential", product);
        return null;
    }

    /// <summary>Both sides trimmed, for the reason <c>stacks.update</c>'s copy spells out.</summary>
    private static bool Changed(string? supplied, string effective) =>
        !string.IsNullOrWhiteSpace(supplied)
        && !string.Equals(supplied.Trim(), effective.Trim(), StringComparison.Ordinal);

    private static string Refusal(string field, Product product) => ProductCatalog.SourceRefusal(field, product);
}
