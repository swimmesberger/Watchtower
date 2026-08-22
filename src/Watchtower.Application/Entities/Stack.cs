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
    /// <summary>
    /// Bearer token the deployed application presents to the public App API (<c>/api/app/*</c>) to
    /// query its own status, version and logs. Injected into every deploy as
    /// <c>WATCHTOWER_APP_TOKEN</c>. Stored in plaintext because it must be re-injected on each
    /// deploy — see <see cref="Services.AppApiTokens"/>. Null until first generated (lazily, at the
    /// next deploy or when an operator opens the App API panel).
    /// </summary>
    public string? AppApiToken { get; set; }
    /// <summary>
    /// When false, every <c>/api/app/*</c> call presenting this stack's token is rejected with 403.
    /// Defaults to true so a freshly created stack can call the API as soon as it is deployed.
    /// </summary>
    public bool AppApiEnabled { get; set; } = true;
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

    /// <summary>When true the backup schedule includes this stack (ADR-0016). Manual runs work regardless.</summary>
    public bool BackupEnabled { get; set; }
    /// <summary>
    /// Optional per-stack schedule: a five-field cron expression (server-local wall clock) that
    /// replaces the instance-wide <c>Backup:Cron</c> for this stack (ADR-0018). Null = follow the
    /// instance schedule.
    /// </summary>
    public string? BackupCron { get; set; }
    /// <summary>
    /// Due time of the last schedule window the scheduler enqueued a backup for — the scheduler's
    /// cursor, so a restart neither fires a window twice nor loses one (ADR-0018). Null until the
    /// first scheduled run; manual runs do not touch it.
    /// </summary>
    public DateTimeOffset? LastScheduledBackupAt { get; set; }
    /// <summary>
    /// When true (default), the stack's running containers are stopped for the duration of the
    /// volume archive step and restarted afterwards, so the snapshot is consistent (ADR-0016 §2).
    /// </summary>
    public bool BackupStopContainers { get; set; } = true;
    /// <summary>
    /// How the containers <see cref="BackupStopContainers"/> selects are quiesced when their service
    /// carries no explicit <c>watchtower.backup.stop</c> label: stopped (default, application-consistent)
    /// or paused (cgroup freeze, milliseconds of downtime, crash-consistent) — ADR-0019.
    /// </summary>
    public BackupQuiesceMode BackupQuiesceMode { get; set; } = BackupQuiesceMode.Stop;

    /// <summary>Set when this stack is a tenant instance of a <see cref="StackTemplate"/>; null for standalone stacks.</summary>
    public int? TemplateId { get; set; }
    public StackTemplate? Template { get; set; }
    /// <summary>The tenant identifier within the template (unique per template); null for standalone stacks.</summary>
    public string? TenantSlug { get; set; }

    public ICollection<DeployEvent> DeployEvents { get; set; } = [];
    public ICollection<StackEnvVar> EnvVars { get; set; } = [];
    public StackUpdateCheck? UpdateCheck { get; set; }
}
