namespace Watchtower.Application.Services;

/// <summary>Progress stage of the self-update apply operation.</summary>
public enum SelfUpdateApplyStage {
    /// <summary>No apply operation is running.</summary>
    Idle,
    /// <summary>Pulling the latest image in the main process.</summary>
    Pulling,
    /// <summary>Coordinator container spawned; waiting for it to recreate this container.</summary>
    Restarting,
    /// <summary>The last apply operation failed.</summary>
    Error,
}

/// <summary>
/// Combined view of Watchtower's self-update configuration and the cached result
/// of the most recent "check for updates" call.
/// </summary>
public sealed record SelfUpdateStatus {
    /// <summary>Registry credential for pulling the Watchtower image; null for public images.</summary>
    public int? CredentialId { get; init; }

    // Auto-detected from the running container (null when not in Docker).
    public string? DetectedImageName { get; init; }
    public required bool IsRunningInContainer { get; init; }

    // Cached check result (null until first check).
    public string? CurrentImageId { get; init; }
    public string? LatestImageId { get; init; }
    public required bool IsOutdated { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>True when Watchtower is running as a container with a detectable image, so "Apply update" can run.</summary>
    public required bool CanApplyUpdate { get; init; }

    /// <summary>Current apply stage as a lowercase string: "idle", "pulling", "restarting", or "error".</summary>
    public required string ApplyStage { get; init; }
    public string? ApplyError { get; init; }

    /// <summary>When this Watchtower process started. Used to display uptime in the UI.</summary>
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>Request to update Watchtower's self-update configuration. Pass null to clear.</summary>
public sealed record UpdateSelfConfig {
    public int? CredentialId { get; init; }
}
