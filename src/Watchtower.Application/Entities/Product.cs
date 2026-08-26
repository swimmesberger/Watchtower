namespace Watchtower.Application.Entities;

/// <summary>
/// Which of the two update mechanisms a product uses (ADR-0026 decision 5, docs/products/design.md
/// §"Two modes, one switch"). Exactly one of them is ever active, and exactly one of them is ever
/// rendered — the Updates panel in <see cref="Git"/> mode, the Version panel in <see cref="Releases"/>
/// mode.
/// </summary>
/// <remarks>
/// Stored as the enum name with <see cref="Git"/> as the schema default, so every row that existed
/// before releases did — and every product created since — is in <see cref="Git"/> mode and therefore
/// deploys byte-for-byte as it did before ADR-0026. That is the back-compat contract, and it is a
/// property of the default rather than of any code path: the release machinery is gated on
/// <see cref="Releases"/> everywhere it exists.
/// </remarks>
public enum ProductReleaseMode {
    /// <summary>
    /// Branch-HEAD clone, registry-digest and git-head polling, today's Updates panel and
    /// <see cref="AutoDeployMode"/> labels. The default, and what every migrated product starts as.
    /// </summary>
    Git,

    /// <summary>
    /// Deploys are releases: latest is the release with the highest <see cref="Release.Id"/>, and a
    /// deploy checks out that release's commit and pins its image digests. Flipped on automatically by
    /// the first accepted release (audited), and revertible by an operator through
    /// <c>products.update</c>.
    /// </summary>
    Releases,
}

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
public sealed class Product : IHasXmin {
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

    /// <summary>
    /// Which update mechanism this product's stacks use (ADR-0026 decision 5). <c>Git</c> until the
    /// first release is accepted, which flips it to <c>Releases</c> in the same transaction that
    /// records the release (<see cref="Services.ReleaseIntakeService"/>); an operator can flip it back
    /// through <c>products.update</c>, and the next accepted release flips it forward again.
    /// </summary>
    /// <remarks>
    /// The one switch every release-aware code path is gated on. While it says <c>Git</c> nothing about
    /// deploying, polling or auto-deploying differs from before ADR-0026 — releases may already exist
    /// and simply sit there as records.
    /// </remarks>
    public ProductReleaseMode ReleaseMode { get; set; } = ProductReleaseMode.Git;

    /// <summary>
    /// Whether the CI-runner orchestrator pushes this product's release configuration to the linked
    /// repository's GitHub Actions config (docs/products/design.md §"Secret sync"): the
    /// <c>WATCHTOWER_URL</c> and <c>WATCHTOWER_PRODUCT_ID</c> variables plus the sealed-box
    /// <c>WATCHTOWER_RELEASE_TOKEN</c> secret. Off by default — a hobby install without an admin PAT
    /// pastes the token by hand instead, and that fallback stays first-class.
    /// </summary>
    /// <remarks>
    /// The secret names are fixed, so at most one product per <see cref="CiRepoId"/> may have this on:
    /// two products of one monorepo would overwrite each other's token every pass. A filtered unique
    /// index on <c>(ci_repo_id) WHERE sync_release_secrets</c> makes that unrepresentable;
    /// <c>ci.setReleaseSecretsSync</c> reports the conflict in words before the index has to.
    /// </remarks>
    public bool SyncReleaseSecrets { get; set; }

    /// <summary>
    /// Hash of the release values last pushed successfully (<c>PublicBaseUrl</c> + product id +
    /// token). The orchestrator re-pushes only when it differs from the hash of the current values, so
    /// a rotated token re-syncs by itself and an unchanged one costs no GitHub call.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of <see cref="CiRepo.RegistrySyncedHash"/>: the two contributors share
    /// a repo and a PAT but nothing else, and a rotated registry credential must not re-push the
    /// release token (or vice versa).
    /// </remarks>
    public string? ActionsSyncedHash { get; set; }

    /// <summary>When the last successful release-secret sync finished.</summary>
    public DateTimeOffset? ActionsSyncedAt { get; set; }

    /// <summary>
    /// Why the last release-secret sync failed; null after a success. Durable, so the CI tab can show
    /// a standing failure (a PAT without Secrets write, an unset <c>Watchtower:PublicBaseUrl</c>)
    /// rather than leaving the operator to wonder why the workflow still 404s. Never blocks runners.
    /// </summary>
    public string? LastActionsSyncError { get; set; }

    /// <summary>
    /// How many releases of this product are kept. Every accepted release runs a pruning pass that
    /// deletes the oldest ones beyond this floor — <see cref="Services.ReleasePruner"/> — so a product
    /// whose CI publishes on every push does not accumulate rows forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The floor is a floor, never a ceiling on what is protected.</b> Four kinds of release are
    /// never pruned however old they are: one a stack pins, one a template names as its
    /// <see cref="StackTemplate.DefaultPinnedReleaseId"/>, one a stack records as its
    /// <see cref="Stack.LastDeployedReleaseId"/>, and one any stored <see cref="DeployEvent"/> still
    /// references. The first would change what a stack deploys, the second would silently clear a fleet
    /// default, and the last two would blank out history — none of them are things housekeeping may do.
    /// So a fleet on old pins keeps more than <see cref="RetainReleases"/> releases, by design.
    /// </para>
    /// <para>
    /// Clamped on read rather than trusted (<see cref="Services.ReleasePruner.Clamp"/>): the column has
    /// no RPC setter yet, so the only way to a value outside the sane range is a hand-edited row — and
    /// a zero or negative one would ask the pruner to delete everything.
    /// </para>
    /// </remarks>
    public int RetainReleases { get; set; } = Services.ReleasePruner.DefaultRetainReleases;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The running copies of this product.</summary>
    public ICollection<Stack> Stacks { get; set; } = [];
    /// <summary>The tenancy templates that instantiate this product.</summary>
    public ICollection<StackTemplate> Templates { get; set; } = [];
    /// <summary>The builds of this product, newest last by id. Deleted with the product.</summary>
    public ICollection<Release> Releases { get; set; } = [];

    /// <inheritdoc />
    /// <remarks>
    /// Mapped by <c>XminConcurrency.UseXminAsConcurrencyToken</c>; see <see cref="IHasXmin"/> for why
    /// this is a real property rather than an EF shadow property. Last, because it is the database's
    /// bookkeeping rather than part of what this entity means.
    /// </remarks>
    public uint Xmin { get; private set; }
}
