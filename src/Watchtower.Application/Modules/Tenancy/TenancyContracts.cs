using System.Text.RegularExpressions;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy;

/// <summary>A stack template projection including how many tenants use it.</summary>
/// <param name="RealmId">
/// The population this category's tenants serve (docs/central-auth/design.md §13). Every route created
/// from the template inherits it, which is what decides who may enter a tenant.
/// </param>
/// <param name="ReleaseMode">
/// The product's update mechanism, <c>"git"</c> or <c>"releases"</c> — invariant 4's predicate, carried
/// here for the same reason <c>StackDto</c> carries it: the Instances roster renders a Version column
/// and a version rollup in one mode and neither in the other, and it must not have to fetch the product
/// to find out which.
/// </param>
/// <remarks>
/// As on <c>StackDto</c>, the four source fields are read-only projections of the effective source
/// since ADR-0026: they live on the product, and <c>products.update</c> is what changes them.
/// </remarks>
public sealed record StackTemplateDto(
    int Id,
    string Name,
    int ProductId,
    string ProductName,
    string RepositoryUrl,
    string ComposeFilePath,
    string Branch,
    string? BranchOverride,
    int? CredentialId,
    string DomainPattern,
    string TargetServiceName,
    int TargetPort,
    int RealmId,
    DateTimeOffset CreatedAt,
    int InstanceCount,
    ReleaseRefDto? DefaultPinnedRelease,
    string ReleaseMode);

/// <summary>
/// A release named on a template or a tenant: enough to render a chip, not enough to need a second call.
/// </summary>
/// <remarks>
/// Deliberately a Tenancy type rather than a reference to the Stacks module's identical
/// <c>StackReleaseRefDto</c>: modules do not reach into each other's contracts (ELMOD002), and a
/// two-field projection is not worth a shared module to own it. The wire shape is the same on purpose,
/// so one frontend type reads both.
/// </remarks>
public sealed record ReleaseRefDto(int Id, string Version);

/// <summary>A template's base environment variable.</summary>
public sealed record TemplateEnvVarDto(int Id, string Key, string Value);

/// <summary>One entry in a batch-replace of a template's base env vars, or a per-tenant override.</summary>
public sealed record TemplateEnvVarInput(string Key, string Value);

/// <summary>A tenant (an instance stack) with its primary domain, last-deploy status and version.</summary>
/// <param name="TrackingMode">
/// <c>"latest"</c> or <c>"pinned"</c>, derived from the pin exactly as <c>StackDto</c> derives it —
/// there is no column (ADR-0026 rejected a <c>TrackingMode</c> enum).
/// </param>
/// <param name="PinnedRelease">The release this tenant is pinned to, or null when it tracks latest.</param>
/// <param name="LastDeployedRelease">The release its last successful deploy applied, when there was one.</param>
public sealed record TenantDto(
    int StackId,
    string TenantSlug,
    string StackName,
    string? Domain,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    string TrackingMode = TenancyMapping.TrackingLatest,
    ReleaseRefDto? PinnedRelease = null,
    ReleaseRefDto? LastDeployedRelease = null);

/// <summary>
/// What <c>templates.setTenantsRelease</c> did: the fleet-wide pin, and the deploys it enqueued.
/// </summary>
/// <param name="TenantCount">How many tenants had their pin written — the number the dialog promised.</param>
/// <param name="Deployed">
/// How many of them were enqueued. Lower than <paramref name="TenantCount"/> when the caller asked for
/// no deploy, and when a tenant is stopped: a stopped stack is pinned successfully and simply not
/// deployed, which is a result to show rather than an error.
/// </param>
/// <param name="DeployEventIds">The tracking events, one per enqueued tenant.</param>
/// <param name="Release">The release the fleet now pins, or null when the pin was cleared.</param>
public sealed record SetTenantsReleaseResultDto(
    int TenantCount,
    int Deployed,
    IReadOnlyList<int> DeployEventIds,
    ReleaseRefDto? Release);

/// <summary>
/// One stack's permission to manage a template's tenants through the public management API
/// (<c>/api/mgmt/*</c>).
/// </summary>
/// <param name="StackId">The granted stack — the one whose App API token unlocks the surface.</param>
/// <param name="StackName">That stack's operator-visible name, so a grant list is readable without a second lookup.</param>
/// <param name="AllowDelete">Whether the grant also permits deprovisioning tenants (and purging their volumes).</param>
/// <param name="CreatedAt">When management was first granted; unchanged by later capability edits.</param>
public sealed record TemplateGrantDto(int StackId, string StackName, bool AllowDelete, DateTimeOffset CreatedAt);

/// <summary>In-memory projection + validation helpers (not translatable to SQL).</summary>
public static partial class TenancyMapping {
    /// <summary>A tenant with no pin follows the product's newest release.</summary>
    /// <remarks>
    /// The same two words <c>StackMapping.TrackingLatest</c>/<c>TrackingPinned</c> put on the wire, spelled
    /// again here rather than referenced across the module boundary — see the remarks on
    /// <see cref="ReleaseRefDto"/>.
    /// </remarks>
    public const string TrackingLatest = "latest";

    /// <inheritdoc cref="TrackingLatest"/>
    public const string TrackingPinned = "pinned";

    /// <summary>
    /// Projects a template. <paramref name="t"/> must have its product loaded — the source fields are
    /// resolved from it (<see cref="ProductSourceResolver"/>) — and its
    /// <see cref="StackTemplate.DefaultPinnedRelease"/> when one should be projected.
    /// </summary>
    public static StackTemplateDto ToDto(StackTemplate t, int instanceCount) {
        var source = ProductSourceResolver.Resolve(t);
        return new StackTemplateDto(
            t.Id, t.Name, t.ProductId, t.Product!.Name,
            source.RepositoryUrl, source.ComposeFilePath, source.Branch, t.BranchOverride, source.CredentialId,
            t.DomainPattern, t.TargetServiceName, t.TargetPort, t.RealmId, t.CreatedAt, instanceCount,
            ReleaseRef(t.DefaultPinnedRelease), t.Product!.ReleaseMode.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// The chip for a release navigation, or null when it is absent — either because there is no such
    /// release or because the caller did not <c>Include</c> it.
    /// </summary>
    public static ReleaseRefDto? ReleaseRef(Release? release) =>
        release is null ? null : new ReleaseRefDto(release.Id, release.Version);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugPattern();

    /// <summary>Lowercases/trims a tenant slug and validates it is DNS-label-safe; null when invalid.</summary>
    public static string? NormalizeSlug(string? slug) {
        var s = slug?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(s) || !SlugPattern().IsMatch(s) ? null : s;
    }

    /// <summary>Substitutes the tenant slug into the template's domain pattern.</summary>
    public static string RenderDomain(string pattern, string slug) =>
        pattern.Replace("{tenant}", slug).Trim().ToLowerInvariant();

    /// <summary>Deterministic, globally-unique compose project name for a tenant stack.</summary>
    public static string ProjectName(string templateName, string slug) {
        var raw = $"{templateName}-{slug}".ToLowerInvariant();
        return SanitizePattern().Replace(raw, "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex SanitizePattern();

    /// <summary>Returns the first duplicate key in the list, or null when all keys are unique.</summary>
    public static string? FirstDuplicateKey(IEnumerable<TemplateEnvVarInput> vars) =>
        vars.GroupBy(v => v.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;

    /// <summary>Merges per-tenant overrides over the template's base env vars (override wins by key).</summary>
    public static IReadOnlyList<TemplateEnvVarInput> MergeEnv(
        IEnumerable<StackTemplateEnvVar> baseVars, IEnumerable<TemplateEnvVarInput>? overrides) {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in baseVars) merged[v.Key] = v.Value;
        foreach (var v in overrides ?? []) merged[v.Key] = v.Value;
        return merged.Select(kv => new TemplateEnvVarInput(kv.Key, kv.Value)).ToList();
    }
}
