namespace Watchtower.Application.Entities;

/// <summary>
/// A reusable definition that is instantiated once per tenant. Each instance is a normal
/// <see cref="Stack"/> (linked via <see cref="Stack.TemplateId"/> and carrying a
/// <see cref="Stack.TenantSlug"/>) with its own isolated containers, network, and volumes — Compose
/// namespaces everything by project name. Creating a tenant points the new stack at the template's
/// <see cref="Product"/> by reference (ADR-0026 — nothing is copied, so source edits propagate),
/// merges the base env vars with per-tenant overrides, and adds a managed route derived from
/// <see cref="DomainPattern"/>.
/// </summary>
public sealed class StackTemplate {
    public int Id { get; set; }

    /// <summary>
    /// The population whose accounts this category's tenants serve (design.md §13): a category lives in
    /// exactly one realm, and every tenant route created from it inherits that realm. Standalone stacks —
    /// those with no template — belong to the system realm. Defaults to the system realm, so a deployment
    /// that never creates a second one behaves exactly as it did before realms existed.
    /// </summary>
    public int RealmId { get; set; } = Realm.SystemRealmId;

    /// <inheritdoc cref="RealmId"/>
    public Realm? Realm { get; set; }

    /// <summary>
    /// Operator-facing name, unique across the whole instance. Deliberately <em>not</em> realm-scoped in
    /// v1: templates are administered from the system realm only, so one flat namespace is what an
    /// operator sees.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The product every tenant of this template runs (ADR-0026). Required and <c>Restrict</c>, like
    /// <see cref="Stack.ProductId"/>. Changing it while the template has tenants is refused — it would
    /// repoint every one of them at a different codebase.
    /// </summary>
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>
    /// Branch this template's tenants deploy instead of <see cref="Entities.Product.DefaultBranch"/>.
    /// Null inherits the product default; an individual tenant may still override it again
    /// (see <see cref="Services.ProductSourceResolver"/>).
    /// </summary>
    public string? BranchOverride { get; set; }

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
