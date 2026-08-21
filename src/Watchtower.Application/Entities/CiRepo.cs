namespace Watchtower.Application.Entities;

/// <summary>
/// A GitHub repository whose Actions jobs run on this Watchtower instance via ephemeral
/// self-hosted runners (see docs/ci-runners/design.md). Runner containers themselves are
/// not persisted — they are tracked via Docker labels and reconciled by the orchestrator.
/// </summary>
public sealed class CiRepo {
    public int Id { get; set; }

    /// <summary>GitHub repository owner (user or org login).</summary>
    public required string Owner { get; set; }

    /// <summary>GitHub repository name (without owner).</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Credential holding the fine-grained PAT (Administration RW, Actions R, Metadata/Contents R).
    /// The PAT never enters a runner container — only single-use JIT configs do.
    /// </summary>
    public int CredentialId { get; set; }
    public Credential? Credential { get; set; }

    /// <summary>When false the orchestrator tears down this repo's runners and spawns none.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of runner slots (max concurrent jobs) for this repo.</summary>
    public int MaxConcurrentRunners { get; set; } = 1;

    /// <summary>Overrides the instance-wide runner image when set.</summary>
    public string? RunnerImage { get; set; }

    /// <summary>Comma-separated extra runner labels beyond the defaults (self-hosted, watchtower, instance).</summary>
    public string? ExtraLabels { get; set; }

    /// <summary>
    /// Mount /var/run/docker.sock into this repo's runners. Host-root equivalent — opt-in,
    /// intended only for trusted private repos whose jobs build/push images.
    /// </summary>
    public bool AllowDockerSocket { get; set; }

    /// <summary>
    /// Detected toolchain profile (serialized <see cref="Services.CiToolchainProfile"/>), refreshed
    /// from the working tree of every stack deploy that clones this repo. Null until a linked stack
    /// deploys. Detection is heuristic and best-effort — an empty/absent profile never blocks runners.
    /// </summary>
    public string? ToolchainProfileJson { get; set; }

    /// <summary>When the toolchain profile was last (re)detected.</summary>
    public DateTimeOffset? ToolchainDetectedAt { get; set; }

    /// <summary>
    /// Hash of the profile the toolcache volume was last successfully pre-warmed for. The
    /// orchestrator re-warms only when this differs from the current profile's hash.
    /// </summary>
    public string? WarmedProfileHash { get; set; }

    /// <summary>When the last successful cache pre-warm finished.</summary>
    public DateTimeOffset? LastWarmedAt { get; set; }

    /// <summary>Tail output of the last failed warmer run; null after a success. Never fatal.</summary>
    public string? LastWarmError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>"owner/name" as used by the GitHub API and container labels.</summary>
    public string FullName => $"{Owner}/{Name}";
}
