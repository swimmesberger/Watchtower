using System.Text.RegularExpressions;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Tenancy;

/// <summary>A stack template projection including how many tenants use it.</summary>
public sealed record StackTemplateDto(
    int Id,
    string Name,
    string RepositoryUrl,
    string ComposeFilePath,
    string Branch,
    int? CredentialId,
    string DomainPattern,
    string TargetServiceName,
    int TargetPort,
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

/// <summary>In-memory projection + validation helpers (not translatable to SQL).</summary>
public static partial class TenancyMapping {
    public static StackTemplateDto ToDto(StackTemplate t, int instanceCount) => new(
        t.Id, t.Name, t.RepositoryUrl, t.ComposeFilePath, t.Branch, t.CredentialId,
        t.DomainPattern, t.TargetServiceName, t.TargetPort, t.CreatedAt, instanceCount);

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
