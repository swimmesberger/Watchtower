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

    /// <summary>
    /// The GitHub repository whose Actions runners and Actions-secret sync belong to this product
    /// (ADR-0026 decision 7). Null when CI was never enabled, when the remote is not on github.com, or
    /// when the link has simply not been resolved yet — it is recorded lazily on the first CI read that
    /// finds a <see cref="Entities.CiRepo"/> matching the parsed <c>owner/name</c>, and cleared by
    /// <c>products.update</c> when the repository URL moves. Set to null when the CI repo is deleted.
    /// </summary>
    /// <remarks>
    /// The FK replaces re-parsing <see cref="RepositoryUrl"/> on every read. <c>CiRepo</c> stays a
    /// separate entity rather than columns here because it is GitHub-specific infrastructure and several
    /// products (a second compose file in the same repository) legitimately share one.
    /// </remarks>
    public int? CiRepoId { get; set; }
    public CiRepo? CiRepo { get; set; }

    /// <summary>
    /// Bearer token the product's CI presents to the release webhook
    /// (<c>POST /api/webhooks/products/{id}/release</c>), prefixed <c>wtrel_</c>. Null until one is
    /// generated, which is also one of the two conditions under which the endpoint answers 404 (see
    /// <see cref="ReleaseWebhookEnabled"/>).
    /// </summary>
    /// <remarks>
    /// Plaintext, like <see cref="Stack.WebhookToken"/> and <see cref="Credential.Token"/>: the value
    /// has to be readable back to be shown for copying and — from the secret-sync stage on — pushed to
    /// the repository's Actions secrets. A hash would make it unrecoverable and force a rotation every
    /// time somebody needed it. Unique across products so a presented token names at most one.
    /// </remarks>
    public string? ReleaseWebhookToken { get; set; }

    /// <summary>
    /// Whether the release webhook accepts calls. False until a token is generated: rotating the token
    /// (<c>products.rotateReleaseToken</c>) enables it, and enabling it
    /// (<c>products.setReleaseWebhook</c>) generates a token when there is none — so "enabled" and
    /// "has a token" only ever come apart in the direction the endpoint treats as closed. Disabling
    /// deliberately keeps the token, so re-enabling does not invalidate the secret already sitting in
    /// somebody's CI configuration.
    /// </summary>
    public bool ReleaseWebhookEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The running copies of this product.</summary>
    public ICollection<Stack> Stacks { get; set; } = [];
    /// <summary>The tenancy templates that instantiate this product.</summary>
    public ICollection<StackTemplate> Templates { get; set; } = [];
    /// <summary>The builds of this product, newest last by id. Deleted with the product.</summary>
    public ICollection<Release> Releases { get; set; } = [];
}
