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
    /// <summary>When the last deploy reached a terminal state (Success or Failed).</summary>
    public DateTimeOffset? LastDeployedAt { get; set; }
    /// <summary>Status of the last deploy.</summary>
    public DeployStatus? LastDeployStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when this stack is a tenant instance of a <see cref="StackTemplate"/>; null for standalone stacks.</summary>
    public int? TemplateId { get; set; }
    public StackTemplate? Template { get; set; }
    /// <summary>The tenant identifier within the template (unique per template); null for standalone stacks.</summary>
    public string? TenantSlug { get; set; }

    public ICollection<DeployEvent> DeployEvents { get; set; } = [];
    public ICollection<StackEnvVar> EnvVars { get; set; } = [];
    public StackUpdateCheck? UpdateCheck { get; set; }
}
