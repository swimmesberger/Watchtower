namespace Watchtower.Application.Entities;

/// <summary>Terminal and in-flight states of a stack deployment.</summary>
public enum DeployStatus {
    /// <summary>Deploy completed successfully.</summary>
    Success,
    /// <summary>Deploy failed.</summary>
    Failed,
    /// <summary>Deploy is in progress.</summary>
    Running,
    /// <summary>Deploy is accepted and waiting behind an already-running deploy for the same stack.</summary>
    Queued,
}

/// <summary>How a stack is redeployed without an inbound webhook (pull-based deployment).</summary>
public enum AutoDeployMode {
    /// <summary>No automatic deploys; webhook/manual only.</summary>
    Off,
    /// <summary>Redeploy as soon as polling detects a new image digest or a new commit on the branch.</summary>
    OnChange,
    /// <summary>Check once per day at <see cref="Stack.AutoDeployTime"/> and redeploy only if something new is available.</summary>
    Scheduled,
}

/// <summary>A named Docker Compose stack backed by a git repository.</summary>
public sealed class Stack {
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string RepositoryUrl { get; set; }
    /// <summary>Path to the compose file within the repository.</summary>
    public required string ComposeFilePath { get; set; }
    public required string Branch { get; set; }
    /// <summary>Value passed to <c>--project-name</c>; defaults to the stack name with spaces hyphenated.</summary>
    public required string ComposeProjectName { get; set; }
    /// <summary>Optional link to a credential used for git cloning. Set to null when the credential is deleted.</summary>
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }
    /// <summary>Bearer token protecting the deploy webhook endpoint. Null when the webhook is unauthenticated.</summary>
    public string? WebhookToken { get; set; }
    /// <summary>When true the webhook endpoint is active; when false it returns 404.</summary>
    public bool WebhookEnabled { get; set; }
    /// <summary>Pull-based deployment mode for hosts where an inbound webhook can't reach Watchtower.</summary>
    public AutoDeployMode AutoDeployMode { get; set; } = AutoDeployMode.Off;
    /// <summary>
    /// Local time of day ("HH:mm") for <see cref="AutoDeployMode.Scheduled"/> — e.g. "02:00".
    /// Null unless the mode is Scheduled.
    /// </summary>
    public string? AutoDeployTime { get; set; }
    /// <summary>
    /// Commit SHA that was checked out by the last successful deploy. Compared against the remote
    /// branch head (git ls-remote) to detect new commits. Null until a deploy succeeds.
    /// </summary>
    public string? LastDeployedCommit { get; set; }
    /// <summary>When the last deploy reached a terminal state (Success or Failed).</summary>
    public DateTimeOffset? LastDeployedAt { get; set; }
    /// <summary>Status of the last deploy.</summary>
    public DeployStatus? LastDeployStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<DeployEvent> DeployEvents { get; set; } = [];
    public ICollection<StackEnvVar> EnvVars { get; set; } = [];
    public StackUpdateCheck? UpdateCheck { get; set; }
}
