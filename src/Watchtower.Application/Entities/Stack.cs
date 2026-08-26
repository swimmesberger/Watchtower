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

/// <summary>
/// The lifecycle state an operator wants a stack in (ADR-0025). Observed container state is read
/// live from Docker; this is the intent Watchtower persists and enforces.
/// </summary>
public enum StackDesiredState {
    /// <summary>Normal operation: the stack's containers run and every deploy path is open.</summary>
    Running,
    /// <summary>
    /// The stack is deliberately stopped ("disabled"): its containers are stopped, deploys are
    /// rejected, and the startup reconcile re-stops containers a Docker restart policy revived.
    /// </summary>
    Stopped,
}

/// <summary>A named Docker Compose stack: one running copy of a <see cref="Entities.Product"/>.</summary>
public sealed class Stack {
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>
    /// The product this stack runs (ADR-0026). Required and <c>Restrict</c>: deleting a product while
    /// anything still deploys it is refused rather than cascaded. The repository URL, compose file path
    /// and clone credential live there, so a source edit reaches every stack of the product.
    /// </summary>
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>
    /// Branch this stack deploys instead of <see cref="Entities.Product.DefaultBranch"/> — how
    /// production-on-<c>main</c> and staging-on-<c>develop</c> share one product. Null inherits, via the
    /// template's own override when the stack is a tenant
    /// (see <see cref="Services.ProductSourceResolver"/>).
    /// </summary>
    public string? BranchOverride { get; set; }
    /// <summary>
    /// The release this stack is pinned to, or null for the default "track latest" (ADR-0026
    /// decision 4). There is deliberately no tracking-mode enum: null <em>is</em> latest-tracking, so
    /// the two states cannot disagree.
    /// </summary>
    /// <remarks>
    /// A pin is an explicit "stay here" and therefore the opt-out from <em>all</em> automation
    /// (design.md §"Auto-deploy precedence", rule 2): release fan-out skips it, the schedule window
    /// skips it, and polling never deployed it in release mode anyway. A manual deploy redeploys the
    /// pin. The foreign key is <c>Restrict</c> on purpose — deleting a pinned release is refused,
    /// naming the stacks, rather than silently flipping them back to latest.
    /// </remarks>
    public int? PinnedReleaseId { get; set; }
    public Release? PinnedRelease { get; set; }

    /// <summary>
    /// The release the last successful deploy actually applied, or null when no release-mode deploy has
    /// succeeded yet. Written at the end of a deploy, from the release resolved at execution time — so
    /// a coalesced deploy records what ran, not what was asked for.
    /// </summary>
    /// <remarks>
    /// The comparison behind three separate behaviours: the release-triggered short-circuit
    /// ("already on this release — nothing to do"), the scheduled-window "is there something newer",
    /// and the update check's <c>AvailableReleaseId</c>. <c>SET NULL</c> rather than <c>Restrict</c>:
    /// this is a record of the past, and deleting an old release must not be refused because something
    /// once deployed it.
    /// </remarks>
    public int? LastDeployedReleaseId { get; set; }
    public Release? LastDeployedRelease { get; set; }

    /// <summary>Value passed to <c>--project-name</c>; defaults to the stack name with spaces hyphenated.</summary>
    public required string ComposeProjectName { get; set; }
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

    /// <summary>
    /// The operator's intent for the stack's lifecycle (ADR-0025). <see cref="StackDesiredState.Stopped"/>
    /// survives Watchtower restarts: every deploy path refuses the stack and the startup reconcile
    /// re-stops containers that a Docker restart policy brought back after a host reboot.
    /// </summary>
    public StackDesiredState DesiredState { get; set; } = StackDesiredState.Running;

    /// <summary>
    /// The remote directory this stack's archives are written to and read from, relative to the storage
    /// provider's base path — <c>{instance}/{stack}</c> for a standalone stack,
    /// <c>{instance}/{product}/{tenant}</c> for a tenant (design.md §"Backups across tenants"). Null on
    /// a stack created before the column existed, which resolves to the computed legacy value instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stamped once and stable thereafter.</b> Renaming a stack no longer orphans its archives — the
    /// pre-existing hazard this column exists to close — and, by the same rule, renaming the Watchtower
    /// <em>instance</em> does not move a stack whose directory is already stamped. That is deliberate:
    /// the directory names where bytes already are, and no rename can move bytes that live on somebody
    /// else's SFTP server. (Before this column, an instance rename orphaned every archive of every
    /// stack, so the new behaviour is strictly better and never worse.)
    /// </para>
    /// <para>
    /// Null means "compute it as we always did" (<see cref="Services.BackupNaming.StackDirectory"/> from
    /// the live instance name and the stack's current name), so every archive an upgraded install
    /// already holds stays discoverable with no migration writing a value the database cannot know. A
    /// legacy stack is stamped with exactly that computed value after its next <em>successful</em>
    /// backup, which is the moment we know the value is the one the storage really uses.
    /// </para>
    /// </remarks>
    public string? BackupDirectory { get; set; }

    /// <summary>
    /// When true the backup schedule includes this stack (ADR-0016); manual runs work regardless. Null
    /// inherits — the template's policy for a tenant, otherwise the instance default (off).
    /// </summary>
    /// <remarks>
    /// Tri-state since stage 7 of ADR-0026, like <see cref="BackupCron"/> has always been. Existing
    /// rows kept their explicit <c>true</c>/<c>false</c> through the migration, so nothing an operator
    /// had configured started inheriting; only rows written afterwards start out null.
    /// Resolve it through <see cref="Services.BackupPolicyResolver"/>, never by reading it directly.
    /// </remarks>
    public bool? BackupEnabled { get; set; }
    /// <summary>
    /// Optional per-stack schedule: a five-field cron expression (server-local wall clock) that
    /// replaces the instance-wide <c>Backup:Cron</c> for this stack (ADR-0018). Null inherits — the
    /// template's expression for a tenant, otherwise the instance schedule.
    /// </summary>
    public string? BackupCron { get; set; }
    /// <summary>
    /// Due time of the last schedule window the scheduler enqueued a backup for — the scheduler's
    /// cursor, so a restart neither fires a window twice nor loses one (ADR-0018). Null until the
    /// first scheduled run; manual runs do not touch it.
    /// </summary>
    public DateTimeOffset? LastScheduledBackupAt { get; set; }
    /// <summary>
    /// When true, the stack's running containers are stopped for the duration of the volume archive
    /// step and restarted afterwards, so the snapshot is consistent (ADR-0016 §2). Null inherits — the
    /// template's policy for a tenant, otherwise the instance default (on).
    /// </summary>
    public bool? BackupStopContainers { get; set; }
    /// <summary>
    /// How the containers <see cref="BackupStopContainers"/> selects are quiesced when their service
    /// carries no explicit <c>watchtower.backup.stop</c> label: stopped (application-consistent) or
    /// paused (cgroup freeze, milliseconds of downtime, crash-consistent) — ADR-0019. Null inherits —
    /// the template's policy for a tenant, otherwise the instance default (stop).
    /// </summary>
    public BackupQuiesceMode? BackupQuiesceMode { get; set; }

    /// <summary>Set when this stack is a tenant instance of a <see cref="StackTemplate"/>; null for standalone stacks.</summary>
    public int? TemplateId { get; set; }
    public StackTemplate? Template { get; set; }
    /// <summary>The tenant identifier within the template (unique per template); null for standalone stacks.</summary>
    public string? TenantSlug { get; set; }

    public ICollection<DeployEvent> DeployEvents { get; set; } = [];
    public ICollection<StackEnvVar> EnvVars { get; set; } = [];
    public StackUpdateCheck? UpdateCheck { get; set; }
}
