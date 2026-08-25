using System.Text.RegularExpressions;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy;

/// <summary>A stack template projection including how many tenants use it.</summary>
/// <param name="RealmId">
/// The population this category's tenants serve (docs/central-auth/design.md §13). Every route created
/// from the template inherits it, which is what decides who may enter a tenant.
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
    int InstanceCount);

/// <summary>A template's base environment variable.</summary>
public sealed record TemplateEnvVarDto(int Id, string Key, string Value);

/// <summary>One entry in a batch-replace of a template's base env vars, or a per-tenant override.</summary>
public sealed record TemplateEnvVarInput(string Key, string Value);

/// <summary>A tenant (an instance stack) with its primary domain and last-deploy status.</summary>
public sealed record TenantDto(
    int StackId,
    string TenantSlug,
    string StackName,
    string? Domain,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt);

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
    /// <summary>
    /// Projects a template. <paramref name="t"/> must have its product loaded — the source fields are
    /// resolved from it (<see cref="ProductSourceResolver"/>).
    /// </summary>
    public static StackTemplateDto ToDto(StackTemplate t, int instanceCount) {
        var source = ProductSourceResolver.Resolve(t);
        return new StackTemplateDto(
            t.Id, t.Name, t.ProductId, t.Product!.Name,
            source.RepositoryUrl, source.ComposeFilePath, source.Branch, t.BranchOverride, source.CredentialId,
            t.DomainPattern, t.TargetServiceName, t.TargetPort, t.RealmId, t.CreatedAt, instanceCount);
    }

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
