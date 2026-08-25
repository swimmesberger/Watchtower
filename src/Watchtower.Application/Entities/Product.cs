namespace Watchtower.Application.Entities;

/// <summary>
/// A git repository that defines a deployable application: where the code lives, which compose file
/// describes it, which branch it tracks by default and which credential clones it (ADR-0026). Every
/// <see cref="Stack"/> and every <see cref="StackTemplate"/> references one — a stack is a running
/// copy of a product, a template is tenancy policy on top of one.
/// </summary>
/// <remarks>
/// The four source columns used to live on both <c>stacks</c> and <c>stack_templates</c>, which is why
/// a template's changes never reached the tenants it had already copied them onto. Nothing is copied
/// any more: a tenant holds the same <see cref="Stack.ProductId"/> its template does, so a source edit
/// reaches every instance at its next deploy by construction.
/// <para>
/// Deliberately <em>not</em> unique on <see cref="RepositoryUrl"/>: a different compose file in the
/// same repository is a different deployable thing, so a second product over the same URL is a
/// legitimate state. The dedupe key used by the backfill and by <c>stacks.create</c>'s find-or-create
/// is the normalized <c>(url, compose path)</c> pair — see <see cref="Services.ProductSourceKey"/>.
/// </para>
/// </remarks>
public sealed class Product {
    public int Id { get; set; }

    /// <summary>Operator-facing name, unique across the instance.</summary>
    public required string Name { get; set; }

    /// <summary>Optional free-text description shown on the product page.</summary>
    public string? Description { get; set; }

    /// <summary>Git remote every stack of this product clones from.</summary>
    public required string RepositoryUrl { get; set; }

    /// <summary>Path to the compose file within the repository.</summary>
    public required string ComposeFilePath { get; set; }

    /// <summary>
    /// Branch a stack deploys unless it — or its template — overrides it
    /// (<see cref="Stack.BranchOverride"/>).
    /// </summary>
    public required string DefaultBranch { get; set; }

    /// <summary>Optional credential used for git cloning. Set to null when the credential is deleted.</summary>
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The running copies of this product.</summary>
    public ICollection<Stack> Stacks { get; set; } = [];
    /// <summary>The tenancy templates that instantiate this product.</summary>
    public ICollection<StackTemplate> Templates { get; set; } = [];
}
