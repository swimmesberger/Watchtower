namespace Watchtower.Application.Services;

/// <summary>
/// User-supplied self-update configuration, persisted as typed JSON under the Global-scope
/// settings key <c>self.config</c>.
/// </summary>
public sealed record SelfUpdateConfig {
    /// <summary>Registry credential for pulling the Watchtower image; null for public images.</summary>
    public int? CredentialId { get; init; }
}

/// <summary>
/// Cached self-update check result plus in-flight apply state, persisted as typed JSON under the
/// Global-scope settings key <c>self.runtime</c>. Superset of the old granular <c>self.*</c> keys.
/// </summary>
public sealed record SelfUpdateRuntime {
    public string? CurrentImageId { get; init; }
    public string? LatestImageId { get; init; }
    public bool IsOutdated { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>Apply stage as a lowercase string: "idle", "pulling", "restarting", or "error".</summary>
    public string ApplyStage { get; init; } = "idle";
    public string? ApplyError { get; init; }
    public string? CoordinatorId { get; init; }
}
