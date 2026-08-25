using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Products;

/// <summary>
/// A product with how much of the estate depends on it — the counts are what make the shared-source
/// consequence of an edit visible before it is made.
/// </summary>
/// <param name="ReleaseWebhookEnabled">
/// Whether the product's CI can report releases. The token itself is deliberately not here: it is a
/// secret, and the catalogue lists every product.
/// </param>
/// <param name="LatestRelease">The newest release, or null while the product has none.</param>
/// <param name="ReleaseMode">
/// <c>"git"</c> or <c>"releases"</c> — the switch that decides which update mechanism this product's
/// stacks use, and which of the two panels their pages render (invariant 4).
/// </param>
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
    int TemplateCount,
    bool ReleaseWebhookEnabled,
    ProductReleaseSummaryDto? LatestRelease,
    string ReleaseMode);

/// <summary>The newest release of a product, as much of it as a header line or a catalogue row needs.</summary>
public sealed record ProductReleaseSummaryDto(int Id, string Version, DateTimeOffset CreatedAt);

/// <summary>
/// One release in the product's list: everything the row renders, with the images left behind the row
/// expansion (<c>products.getRelease</c>) so a 20-row page is one query and not twenty.
/// </summary>
/// <param name="CreatedVia">How it arrived: <c>webhook</c> or <c>manual</c>.</param>
/// <param name="PublishedAt">When the build was published, if the reporter said. Display only — the list is ordered by id.</param>
public sealed record ReleaseDto(
    int Id,
    string Version,
    string? CommitSha,
    string Branch,
    string CreatedVia,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string? SourceRunUrl,
    int ImageCount);

/// <summary>One image a release pins.</summary>
public sealed record ReleaseImageDto(string Repository, string? Tag, string Digest);

/// <summary>A release with the images it pins and its notes — the expanded row.</summary>
public sealed record ReleaseDetailDto(
    int Id,
    int ProductId,
    string ProductName,
    string Version,
    string? CommitSha,
    string Branch,
    string CreatedVia,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string? SourceRunUrl,
    string? Notes,
    IReadOnlyList<ReleaseImageDto> Images);

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

    public static ProductDto ToDto(
        Product p, string? credentialName, int stackCount, int templateCount,
        ProductReleaseSummaryDto? latestRelease = null) => new(
        p.Id, p.Name, p.Description, p.RepositoryUrl, p.ComposeFilePath, p.DefaultBranch,
        p.CredentialId, credentialName, p.CreatedAt, stackCount, templateCount,
        p.ReleaseWebhookEnabled, latestRelease, ReleaseModeToDto(p.ReleaseMode));

    /// <summary>Enum → lowercase wire value: "git", "releases".</summary>
    public static string ReleaseModeToDto(ProductReleaseMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>Parses the wire value; null when it is not one of the two names.</summary>
    /// <remarks>
    /// An explicit switch rather than <c>Enum.TryParse</c>, which also accepts the underlying numbers
    /// ("0", "1") and any future member's name — turning the wire contract into "whatever the enum
    /// happens to contain" and letting a caller select a mode by ordinal, which no client should be able
    /// to do. Casing is tolerated because the wire form is lower-case and hand-written JSON is not.
    /// </remarks>
    public static ProductReleaseMode? ParseReleaseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch {
        "git" => ProductReleaseMode.Git,
        "releases" => ProductReleaseMode.Releases,
        _ => null,
    };

    /// <summary>The list row for a release; <paramref name="imageCount"/> is counted by the query.</summary>
    public static ReleaseDto ToDto(Release r, int imageCount) => new(
        r.Id, r.Version, r.CommitSha, r.Branch, r.CreatedVia, r.CreatedAt, r.PublishedAt,
        r.SourceRunUrl, imageCount);

    /// <summary>The expanded release, images ordered by repository so the table is stable between reads.</summary>
    public static ReleaseDetailDto ToDetailDto(Release r, string productName) => new(
        r.Id, r.ProductId, productName, r.Version, r.CommitSha, r.Branch, r.CreatedVia, r.CreatedAt,
        r.PublishedAt, r.SourceRunUrl, r.Notes,
        [.. r.Images
            .OrderBy(i => i.Repository, StringComparer.Ordinal)
            .Select(i => new ReleaseImageDto(i.Repository, i.Tag, i.Digest))]);

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
