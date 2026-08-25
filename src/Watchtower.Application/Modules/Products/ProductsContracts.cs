using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Products;

/// <summary>
/// A product with how much of the estate depends on it — the counts are what make the shared-source
/// consequence of an edit visible before it is made.
/// </summary>
public sealed record ProductDto(
    int Id,
    string Name,
    string? Description,
    string RepositoryUrl,
    string ComposeFilePath,
    string DefaultBranch,
    int? CredentialId,
    string? CredentialName,
    DateTimeOffset CreatedAt,
    int StackCount,
    int TemplateCount);

/// <summary>One stack running a product, with the branch it actually deploys.</summary>
/// <param name="Branch">The effective branch — the override when there is one, else the product default.</param>
/// <param name="TenantSlug">Set when the stack is a tenant of <paramref name="TemplateId"/>; null for standalone stacks.</param>
public sealed record ProductStackDto(
    int Id,
    string Name,
    string Branch,
    string? BranchOverride,
    int? TemplateId,
    string? TenantSlug,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt);

/// <summary>One template instantiating a product, with the branch its tenants deploy.</summary>
public sealed record ProductTemplateDto(
    int Id,
    string Name,
    string Branch,
    string? BranchOverride,
    int TenantCount);

/// <summary>In-memory projection helpers (not translatable to SQL).</summary>
public static class ProductMapping {
    /// <summary>Audit category every product write is recorded under.</summary>
    public const string AuditCategory = Services.ProductCatalog.AuditCategory;

    public static ProductDto ToDto(Product p, string? credentialName, int stackCount, int templateCount) => new(
        p.Id, p.Name, p.Description, p.RepositoryUrl, p.ComposeFilePath, p.DefaultBranch,
        p.CredentialId, credentialName, p.CreatedAt, stackCount, templateCount);

    /// <summary>
    /// Validates and normalizes the fields every product write shares. Returns an error message, or
    /// null when the trimmed values in the <c>out</c> parameters are usable.
    /// </summary>
    public static string? Validate(
        string? name, string? repositoryUrl, string? composeFilePath, string? defaultBranch,
        out string trimmedName, out string trimmedRepositoryUrl,
        out string trimmedComposeFilePath, out string trimmedDefaultBranch) {
        trimmedName = name?.Trim() ?? string.Empty;
        trimmedRepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
        trimmedComposeFilePath = composeFilePath?.Trim() ?? string.Empty;
        trimmedDefaultBranch = defaultBranch?.Trim() ?? string.Empty;

        if (trimmedName.Length == 0) return "Product name is required.";
        if (trimmedRepositoryUrl.Length == 0) return "Repository URL is required.";
        if (trimmedComposeFilePath.Length == 0) return "Compose file path is required.";
        if (trimmedDefaultBranch.Length == 0) return "Default branch is required.";
        return null;
    }
}
