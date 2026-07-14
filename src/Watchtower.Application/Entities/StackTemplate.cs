namespace Watchtower.Application.Entities;

/// <summary>
/// A reusable definition that is instantiated once per tenant. Each instance is a normal
/// <see cref="Stack"/> (linked via <see cref="Stack.TemplateId"/> and carrying a
/// <see cref="Stack.TenantSlug"/>) with its own isolated containers, network, and volumes — Compose
/// namespaces everything by project name. Creating a tenant copies the template's repo/compose/branch
/// into a new stack, merges the base env vars with per-tenant overrides, and adds a managed route
/// derived from <see cref="DomainPattern"/>.
/// </summary>
public sealed class StackTemplate {
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string RepositoryUrl { get; set; }
    /// <summary>Path to the compose file within the repository.</summary>
    public required string ComposeFilePath { get; set; }
    public required string Branch { get; set; }
    /// <summary>Optional git credential, copied onto each tenant stack. Null when the credential is deleted.</summary>
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }

    /// <summary>Domain template for tenants, with a <c>{tenant}</c> placeholder, e.g. <c>{tenant}.example.com</c>.</summary>
    public required string DomainPattern { get; set; }
    /// <summary>Compose service each tenant's route forwards to.</summary>
    public required string TargetServiceName { get; set; }
    /// <summary>Container port the target service listens on.</summary>
    public int TargetPort { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Shared environment defaults; tenant overrides are merged over these at creation.</summary>
    public ICollection<StackTemplateEnvVar> BaseEnvVars { get; set; } = [];
    /// <summary>The tenant stacks created from this template.</summary>
    public ICollection<Stack> Instances { get; set; } = [];
}

/// <summary>A shared environment default on a <see cref="StackTemplate"/>.</summary>
public sealed class StackTemplateEnvVar {
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public StackTemplate? Template { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}
