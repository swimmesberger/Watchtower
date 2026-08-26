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
/// <param name="CommitSha">
/// The commit it was built from, when it records one. Here so the product page can say which commit the
/// latest release actually pins — the other half of the "latest ≠ branch head" comparison.
/// </param>
public sealed record ProductReleaseSummaryDto(
    int Id, string Version, DateTimeOffset CreatedAt, string? CommitSha);

/// <summary>
/// A release named on a stack: enough to render a chip, not enough to need a second call.
/// </summary>
/// <remarks>
/// A Products type rather than a reference to the Stacks module's identical <c>StackReleaseRefDto</c>:
/// modules do not reach into each other's contracts (ELMOD002). Same wire shape on purpose, so one
/// frontend type reads all three.
/// </remarks>
public sealed record ProductReleaseRefDto(int Id, string Version);

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

/// <summary>One stack running a product, with the branch it actually deploys and the version it runs.</summary>
/// <param name="Branch">The effective branch — the override when there is one, else the product default.</param>
/// <param name="TenantSlug">Set when the stack is a tenant of <paramref name="TemplateId"/>; null for standalone stacks.</param>
/// <param name="TrackingMode">
/// <c>"latest"</c> or <c>"pinned"</c>, derived from the pin exactly as <c>StackDto</c> derives it.
/// </param>
/// <param name="PinnedRelease">The release this stack is pinned to, or null when it tracks latest.</param>
/// <param name="LastDeployedRelease">The release its last successful deploy applied, when there was one.</param>
public sealed record ProductStackDto(
    int Id,
    string Name,
    string Branch,
    string? BranchOverride,
    int? TemplateId,
    string? TenantSlug,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    string TrackingMode,
    ProductReleaseRefDto? PinnedRelease,
    ProductReleaseRefDto? LastDeployedRelease);

/// <summary>
/// One stack's line in a release's roll-out view (<c>products.getReleaseRollout</c>).
/// </summary>
/// <param name="Status">
/// The deploy event's status — <c>queued</c>, <c>running</c>, <c>success</c> or <c>failed</c> — or
/// <see cref="ReleaseRolloutDto.SkippedStatus"/> for a stack the roll-out never reached.
/// </param>
/// <param name="SkipReason">
/// Why a skipped stack was not targeted; null for a stack that has a deploy event. Exactly one of the
/// three constants the handler owns — <c>GetReleaseRollout.SkippedStopped</c> (<c>"stopped"</c>),
/// <c>SkippedPinned</c> (<c>"pinned"</c>) or <c>SkippedNotDeployed</c> (<c>"not deployed"</c>). See the
/// honesty note on <see cref="ReleaseRolloutDto"/> for why these describe the stack *now*.
/// </param>
public sealed record ReleaseRolloutStackDto(
    int StackId,
    string StackName,
    string? TenantSlug,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? DeployEventId,
    string? SkipReason);

/// <summary>
/// What one release actually reached: the per-stack rows and the counts above them
/// (docs/products/design.md §"Convergent fan-out", partial failure).
/// </summary>
/// <remarks>
/// <b>The deploy rows are history; the skipped rows are <em>now</em>.</b> A stack with a
/// <c>DeployEvent</c> for this release is a fact about what happened. A stack without one is reported as
/// skipped with the reason its <em>current</em> state gives — stopped, pinned elsewhere, or automation
/// off — which is not necessarily why it was skipped when the fan-out ran: a stack pinned this morning
/// reads as "pinned" even if it was running latest at the time. There is no enqueue-time record to read
/// instead (the fan-out deliberately stores nothing per skipped stack — that is what keeps a 200-tenant
/// release from writing 200 rows of noise), so the view says what is true today and this remark is the
/// contract.
/// </remarks>
/// <param name="Succeeded">Stacks whose newest deploy for this release succeeded.</param>
/// <param name="Failed">…failed. What "Retry failed" re-enqueues.</param>
/// <param name="Queued">…is still waiting behind the deploy gate.</param>
/// <param name="Running">…is deploying right now.</param>
/// <param name="Skipped">Stacks of the product with no deploy event for this release at all.</param>
public sealed record ReleaseRolloutDto(
    int ReleaseId,
    string Version,
    int Succeeded,
    int Failed,
    int Queued,
    int Running,
    int Skipped,
    IReadOnlyList<ReleaseRolloutStackDto> Stacks) {
    /// <summary>The synthetic <c>Status</c> of a stack the roll-out never reached.</summary>
    public const string SkippedStatus = "skipped";
}

/// <summary>
/// The four values <c>DeployEvent.Status</c> takes. Free text in the column since long before ADR-0026
/// and written as literals by the deploy queue; named here so the rollout view's counts and
/// <c>products.retryFailedRollout</c>'s targeting cannot disagree about how <c>failed</c> is spelled.
/// </summary>
public static class DeployEventStatus {
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "success";
    public const string Failed = "failed";
}

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

    /// <summary>A stack with no pin follows the product's newest release.</summary>
    /// <remarks>
    /// The same two words <c>StackMapping.TrackingLatest</c>/<c>TrackingPinned</c> put on the wire,
    /// spelled again here rather than referenced across the module boundary — see the remarks on
    /// <see cref="ProductReleaseRefDto"/>.
    /// </remarks>
    public const string TrackingLatest = "latest";

    /// <inheritdoc cref="TrackingLatest"/>
    public const string TrackingPinned = "pinned";

    /// <summary>The chip for a release, or null when there is none.</summary>
    public static ProductReleaseRefDto? ReleaseRef(int? id, string? version) =>
        id is { } releaseId && version is not null ? new ProductReleaseRefDto(releaseId, version) : null;

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
