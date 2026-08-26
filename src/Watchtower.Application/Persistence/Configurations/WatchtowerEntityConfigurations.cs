using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Persistence.Configurations;

/// <summary>
/// EF Core model configuration for every Watchtower entity. Each class is discovered by the
/// Elarion EF generator via <c>[EntityConfiguration]</c> and applied by the generated
/// <c>ConfigureEntities</c> method on <see cref="WatchtowerDbContext"/>. Column names are
/// snake_cased by convention (<c>UseSnakeCaseNamingConvention</c>); table names are set explicitly.
/// </summary>
/// <remarks>
/// Entities several writers can meet on — <c>Realm</c>, <c>Stack</c>, <c>Route</c>, <c>Group</c> —
/// carry PostgreSQL's <c>xmin</c> system column as their EF concurrency token (ADR-0024 decision 3),
/// so a read-modify-write whose row moved underneath it raises <c>DbUpdateConcurrencyException</c>
/// instead of silently overwriting. <c>xmin</c> rather than a version column of our own because the
/// database maintains it already: a token nobody has to remember to bump cannot be forgotten on the
/// one write path where it mattered. <c>User</c> is the exception, and carries Identity's own
/// <c>ConcurrencyStamp</c> instead — see <c>UserConfiguration</c>.
/// </remarks>
[EntityConfiguration]
public sealed class RealmConfiguration : IEntityTypeConfiguration<Realm> {
    public void Configure(EntityTypeBuilder<Realm> b) {
        b.ToTable("realms");
        b.HasKey(x => x.Id);
        b.UseXminAsConcurrencyToken();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Slug).IsRequired();
        // The slug is the realm's identity on the wire (the `realm` JWT claim), so it is unique and the
        // handlers refuse to change it.
        b.HasIndex(x => x.Slug).IsUnique();
        // At most one realm may name a given route as its login route: the host decides which population
        // a visitor arriving on it authenticates into, so two realms sharing one would make that
        // ambiguous. Filtered rather than plain unique because "no login route yet" is a legitimate state
        // for any number of realms — PostgreSQL already treats NULLs as distinct, but the filter states
        // the intent (the RouteAccessGrant precedent) rather than relying on it.
        b.HasIndex(x => x.LoginRouteId).IsUnique().HasFilter("\"login_route_id\" IS NOT NULL");
        // SET NULL, not Restrict: deleting the route is a legitimate act whose consequence (this realm
        // has no login host any more) is reported by the handler rather than prevented by the schema.
        b.HasOne(x => x.LoginRoute)
            .WithMany()
            .HasForeignKey(x => x.LoginRouteId)
            .OnDelete(DeleteBehavior.SetNull);
        // The operator realm, with the explicit id every realm column defaults to. On the model rather
        // than hand-written into a migration, so the row is part of the schema every environment
        // scaffolds and `migrations has-pending-model-changes` covers it. Its login route is null and
        // stays null on a fresh install: the operator realm falls back to Watchtower:Auth:Host until a
        // Watchtower route is created for it (ADR-0023).
        b.HasData(new Realm {
            Id = Realm.SystemRealmId,
            Name = Realm.SystemRealmName,
            Slug = Realm.SystemRealmSlug,
            IsSystem = true,
            CreatedAt = SystemRealmCreatedAt,
        });
    }

    /// <summary>
    /// Timestamp stamped on the seeded system realm. A literal, not <c>DateTimeOffset.UtcNow</c>: seed
    /// data must produce the same row on every instance, and "when this deployment happened to be
    /// installed" is not a fact about the operator population.
    /// </summary>
    private static readonly DateTimeOffset SystemRealmCreatedAt = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
}

[EntityConfiguration]
public sealed class CredentialConfiguration : IEntityTypeConfiguration<Credential> {
    public void Configure(EntityTypeBuilder<Credential> b) {
        b.ToTable("credentials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Username).IsRequired();
        b.Property(x => x.Token).IsRequired();
        b.HasIndex(x => x.Name);
    }
}

[EntityConfiguration]
public sealed class CiRepoConfiguration : IEntityTypeConfiguration<CiRepo> {
    public void Configure(EntityTypeBuilder<CiRepo> b) {
        b.ToTable("ci_repos");
        b.HasKey(x => x.Id);
        b.Property(x => x.Owner).IsRequired();
        b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => new { x.Owner, x.Name }).IsUnique();
        // GitHub treats owner/name case-insensitively and the lookups do too, so they compare
        // lower(owner)/lower(name). EF cannot model an expression index, so the matching
        // ix_ci_repos_owner_name_lower is raw SQL in the initial migration — if this pair of columns
        // ever moves, that statement moves with it.
        b.HasOne(x => x.Credential)
            .WithMany()
            .HasForeignKey(x => x.CredentialId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.FullName);
    }
}

[EntityConfiguration]
public sealed class RegistryConfiguration : IEntityTypeConfiguration<Registry> {
    public void Configure(EntityTypeBuilder<Registry> b) {
        b.ToTable("registries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Url).IsRequired();
        b.HasIndex(x => x.Name);
        b.HasOne(x => x.Credential)
            .WithMany()
            .HasForeignKey(x => x.CredentialId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

[EntityConfiguration]
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product> {
    public void Configure(EntityTypeBuilder<Product> b) {
        b.ToTable("products");
        b.HasKey(x => x.Id);
        // Several writers meet on a product (the edit handler today; the release webhook and the
        // Actions-secret sync in later stages of ADR-0026), so it carries xmin like Stack and Realm.
        b.UseXminAsConcurrencyToken();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.RepositoryUrl).IsRequired();
        b.Property(x => x.ComposeFilePath).IsRequired();
        b.Property(x => x.DefaultBranch).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        // Deliberately no unique index on the repository URL: a second compose file in the same
        // repository is a second product (ADR-0026 decision 2).
        b.HasOne(x => x.Credential)
            .WithMany()
            .HasForeignKey(x => x.CredentialId)
            .OnDelete(DeleteBehavior.SetNull);
        // SetNull, and deliberately not unique: removing a repo from CI must not take the products that
        // deploy it, and several products (different compose files in one repository) share one CI repo.
        b.HasOne(x => x.CiRepo)
            .WithMany()
            .HasForeignKey(x => x.CiRepoId)
            .OnDelete(DeleteBehavior.SetNull);
        // The release webhook looks a product up by the presented bearer token, so the column is
        // indexed; unique so one token can never name two products. PostgreSQL treats NULLs as
        // distinct, so any number of products may still have no token.
        b.HasIndex(x => x.ReleaseWebhookToken).IsUnique();
        // Stored as the enum name ("Git"/"Releases"). The default is on the model, not only in the
        // migration, so every environment scaffolds a schema in which a product written without one is
        // in Git mode — the back-compat contract of ADR-0026 decision 5 expressed as a column default
        // rather than as something the code has to remember.
        b.Property(x => x.ReleaseMode).HasConversion<string>().HasDefaultValue(ProductReleaseMode.Git);
        // The retention floor, defaulted on the model as well as in the migration so a product written
        // by any path keeps 50 releases rather than 0 — which is what an unset int would ask the pruner
        // to keep. ReleasePruner.Clamp is the second line of defence against a hand-edited row.
        b.Property(x => x.RetainReleases).HasDefaultValue(ReleasePruner.DefaultRetainReleases);
        // The monorepo rule of docs/products/design.md §"Secret sync", as a schema fact: the Actions
        // secret names (WATCHTOWER_URL / WATCHTOWER_PRODUCT_ID / WATCHTOWER_RELEASE_TOKEN) are fixed, so
        // two products of one repository both syncing would overwrite each other's token on every pass.
        // Filtered, because sharing a CI repo is otherwise entirely normal (the plain FK index above
        // stays), and because "not syncing" must remain unconstrained. The handler reports the conflict
        // in words; this is what makes the state unrepresentable.
        //
        // Two declarations over one column, both spelled out. Declaring the filtered index alone would
        // suppress the convention index EF creates for the FK — the convention only fires when nothing
        // already indexes those properties — and the plain lookup the relationship needs would silently
        // disappear. The second one needs a model name *and* a database name, because the snake-case
        // convention derives the database name from the columns and both would otherwise collide on
        // ix_products_ci_repo_id.
        b.HasIndex(x => x.CiRepoId);
        b.HasIndex(x => x.CiRepoId, "ix_products_ci_repo_id_sync_release_secrets")
            .IsUnique()
            .HasFilter("\"sync_release_secrets\"")
            .HasDatabaseName("ix_products_ci_repo_id_sync_release_secrets");
    }
}

[EntityConfiguration]
public sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release> {
    public void Configure(EntityTypeBuilder<Release> b) {
        b.ToTable("releases");
        b.HasKey(x => x.Id);
        b.Property(x => x.Version).IsRequired();
        b.Property(x => x.Branch).IsRequired();
        b.Property(x => x.Fingerprint).IsRequired();
        b.Property(x => x.CreatedVia).IsRequired();
        // The two rules release intake is built on. Unique on version because it is the label an
        // operator picks a release by, and unique on the fingerprint because that is the idempotency
        // key — the pre-checks in ReleaseIntakeService exist for the message, these indexes are what
        // make two concurrent identical webhook calls produce one release.
        b.HasIndex(x => new { x.ProductId, x.Version }).IsUnique();
        b.HasIndex(x => new { x.ProductId, x.Fingerprint }).IsUnique();
        // The listing query: newest-first keyset paging within one product
        // (`WHERE product_id = @p AND id < @before ORDER BY id DESC`). Neither unique index above can
        // serve it, because both carry a non-id second column.
        b.HasIndex(x => new { x.ProductId, x.Id });
        // Cascade: a release is meaningless without its product, and products.delete already refuses
        // while any stack or template still references one — so this only ever fires for a product
        // nothing deploys.
        b.HasOne(x => x.Product)
            .WithMany(p => p.Releases)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class ReleaseImageConfiguration : IEntityTypeConfiguration<ReleaseImage> {
    public void Configure(EntityTypeBuilder<ReleaseImage> b) {
        b.ToTable("release_images");
        b.HasKey(x => x.Id);
        b.Property(x => x.Repository).IsRequired();
        b.Property(x => x.Digest).IsRequired();
        // One build produces one image per repository; two rows for the same repository would leave
        // "which digest does this release pin for ghcr.io/acme/api?" with two answers.
        b.HasIndex(x => new { x.ReleaseId, x.Repository }).IsUnique();
        b.HasOne(x => x.Release)
            .WithMany(r => r.Images)
            .HasForeignKey(x => x.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class StackConfiguration : IEntityTypeConfiguration<Stack> {
    public void Configure(EntityTypeBuilder<Stack> b) {
        b.ToTable("stacks");
        b.HasKey(x => x.Id);
        b.UseXminAsConcurrencyToken();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.ComposeProjectName).IsRequired();
        // Stored as the enum name (e.g. "Success"); the API maps it to lowercase for the client.
        b.Property(x => x.LastDeployStatus).HasConversion<string>();
        // Stored as the enum name (e.g. "OnChange"); the API maps it to camelCase for the client.
        b.Property(x => x.AutoDeployMode).HasConversion<string>();
        // Stored as the enum name ("Stop"/"Pause"); the API maps it to lowercase. The default is on the
        // model so a row written without one — by the SQLite importer, say — stops its containers, which
        // is what every stack did before quiesce modes existed.
        b.Property(x => x.BackupQuiesceMode).HasConversion<string>().HasDefaultValue(BackupQuiesceMode.Stop);
        // Stored as the enum name ("Running"/"Stopped"); the API maps it to lowercase. The default is
        // on the model so rows written without one — by the SQLite importer, say — count as running,
        // which is what every stack was before desired state existed (ADR-0025).
        b.Property(x => x.DesiredState).HasConversion<string>().HasDefaultValue(StackDesiredState.Running);
        b.HasIndex(x => x.Name).IsUnique();
        // The compose project name is compared case-insensitively (StackProjectNames.IsTakenAsync) but
        // stored as the operator typed it, so the supporting index is on lower(compose_project_name).
        // EF cannot model an expression index: ix_stacks_compose_project_name_lower is raw SQL in the
        // initial migration, and moves with this column if it ever does.
        // The App API authenticates every request by looking the presented bearer token up here, so
        // the column must be indexed. Unique guards against two stacks ever sharing a token; PostgreSQL
        // treats NULLs as distinct, so any number of stacks may still have no token yet.
        b.HasIndex(x => x.AppApiToken).IsUnique();
        // Restrict, like a realm's categories: deleting a product must not silently take every stack
        // deploying it. The products.delete handler refuses while anything still references it, and
        // names the blockers.
        b.HasOne(x => x.Product)
            .WithMany(p => p.Stacks)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        // Tenant instances link back to their template; deleting a template detaches (not deletes) them.
        // (TemplateId, TenantSlug) is unique — PostgreSQL treats NULLs as distinct, so standalone stacks
        // (both null) never collide.
        b.HasOne(x => x.Template)
            .WithMany(t => t.Instances)
            .HasForeignKey(x => x.TemplateId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.TemplateId, x.TenantSlug }).IsUnique();
        // Restrict (ADR-0026 decision 4): deleting a release a stack pins would silently flip that stack
        // back to latest-tracking — a deploy-behaviour change caused by a delete somewhere else.
        // products.deleteRelease refuses first and names the stacks; this is the backstop.
        b.HasOne(x => x.PinnedRelease)
            .WithMany()
            .HasForeignKey(x => x.PinnedReleaseId)
            .OnDelete(DeleteBehavior.Restrict);
        // SetNull, unlike the pin: this records what once ran, and pruning an old release must not be
        // refused because a stack still remembers deploying it.
        b.HasOne(x => x.LastDeployedRelease)
            .WithMany()
            .HasForeignKey(x => x.LastDeployedReleaseId)
            .OnDelete(DeleteBehavior.SetNull);
        // The fan-out predicate and products.deleteRelease's guard both look stacks up by the release
        // they pin; PostgreSQL treats NULLs as distinct, so latest-tracking stacks cost nothing here.
        b.HasIndex(x => x.PinnedReleaseId);
    }
}

[EntityConfiguration]
public sealed class StackTemplateConfiguration : IEntityTypeConfiguration<StackTemplate> {
    public void Configure(EntityTypeBuilder<StackTemplate> b) {
        b.ToTable("stack_templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.DomainPattern).IsRequired();
        b.Property(x => x.TargetServiceName).IsRequired();
        // Deliberately global, not (realm_id, name): a template name is what an operator picks a category
        // by in the one management surface there is, and that surface is system-realm-only (design.md §13).
        b.HasIndex(x => x.Name).IsUnique();
        // Restrict, not cascade: deleting a realm must not silently take its categories — and with them
        // every tenant stack — away. The realms.delete handler refuses while anything still references it.
        b.HasOne(x => x.Realm)
            .WithMany()
            .HasForeignKey(x => x.RealmId)
            .OnDelete(DeleteBehavior.Restrict);
        // Restrict for the same reason as on Stack: a product delete that took its templates — and
        // with them every tenant — would be a blast radius discovered afterwards.
        b.HasOne(x => x.Product)
            .WithMany(p => p.Templates)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        // SetNull, not Restrict like Stack.PinnedReleaseId: this pins nothing that is running, so a
        // delete that clears it changes no deploy — the next tenant simply tracks latest. What must not
        // clear it silently is *pruning*, which is a rule in ReleasePruner rather than a schema one,
        // because retention is the only deleter that would ever reach an old default by accident.
        b.HasOne(x => x.DefaultPinnedRelease)
            .WithMany()
            .HasForeignKey(x => x.DefaultPinnedReleaseId)
            .OnDelete(DeleteBehavior.SetNull);
        // The pruner's template-default protection query looks templates up by the release they name.
        b.HasIndex(x => x.DefaultPinnedReleaseId);
    }
}

[EntityConfiguration]
public sealed class StackTemplateEnvVarConfiguration : IEntityTypeConfiguration<StackTemplateEnvVar> {
    public void Configure(EntityTypeBuilder<StackTemplateEnvVar> b) {
        b.ToTable("stack_template_env_vars");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).IsRequired();
        b.Property(x => x.Value).IsRequired();
        b.HasIndex(x => new { x.TemplateId, x.Key }).IsUnique();
        b.HasOne(x => x.Template)
            .WithMany(t => t.BaseEnvVars)
            .HasForeignKey(x => x.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class DeployEventConfiguration : IEntityTypeConfiguration<DeployEvent> {
    public void Configure(EntityTypeBuilder<DeployEvent> b) {
        b.ToTable("deploy_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.TriggeredBy).IsRequired();
        b.Property(x => x.Status).IsRequired();
        b.HasIndex(x => new { x.StackId, x.StartedAt });
        b.HasIndex(x => x.Status);
        b.HasOne(x => x.Stack)
            .WithMany(s => s.DeployEvents)
            .HasForeignKey(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
        // SetNull: the rollout view groups deploy events by release, but a deleted release must not
        // take the history of what it deployed with it.
        b.HasOne(x => x.Release)
            .WithMany()
            .HasForeignKey(x => x.ReleaseId)
            .OnDelete(DeleteBehavior.SetNull);
        // The rollout view's grouping (succeeded / failed / queued per release).
        b.HasIndex(x => x.ReleaseId);
    }
}

[EntityConfiguration]
public sealed class BackupEventConfiguration : IEntityTypeConfiguration<BackupEvent> {
    public void Configure(EntityTypeBuilder<BackupEvent> b) {
        b.ToTable("backup_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.TriggeredBy).IsRequired();
        b.Property(x => x.Status).IsRequired();
        // The history view reads newest-first per stack; the startup sweep scans by status.
        b.HasIndex(x => new { x.StackId, x.StartedAt });
        b.HasIndex(x => x.Status);
        b.HasOne(x => x.Stack)
            .WithMany()
            .HasForeignKey(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class BackupPausedContainerConfiguration : IEntityTypeConfiguration<BackupPausedContainer> {
    public void Configure(EntityTypeBuilder<BackupPausedContainer> b) {
        b.ToTable("backup_paused_containers");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContainerId).IsRequired();
        b.Property(x => x.ContainerName).IsRequired();
        b.Property(x => x.StackName).IsRequired();
        // The run deletes its rows by container id once the container is unpaused.
        b.HasIndex(x => x.ContainerId);
    }
}

[EntityConfiguration]
public sealed class StackBackupServiceOverrideConfiguration : IEntityTypeConfiguration<StackBackupServiceOverride> {
    public void Configure(EntityTypeBuilder<StackBackupServiceOverride> b) {
        b.ToTable("stack_backup_service_overrides");
        b.HasKey(x => x.Id);
        b.Property(x => x.Service).IsRequired();
        // One row per (stack, service): setting an override upserts, clearing every knob deletes it.
        b.HasIndex(x => new { x.StackId, x.Service }).IsUnique();
        b.HasOne(x => x.Stack)
            .WithMany()
            .HasForeignKey(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class StackEnvVarConfiguration : IEntityTypeConfiguration<StackEnvVar> {
    public void Configure(EntityTypeBuilder<StackEnvVar> b) {
        b.ToTable("stack_env_vars");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).IsRequired();
        b.Property(x => x.Value).IsRequired();
        b.HasIndex(x => new { x.StackId, x.Key }).IsUnique();
        b.HasOne(x => x.Stack)
            .WithMany(s => s.EnvVars)
            .HasForeignKey(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class RouteConfiguration : IEntityTypeConfiguration<Route> {
    public void Configure(EntityTypeBuilder<Route> b) {
        // The two route kinds are different rows, and the schema says which columns each one may fill
        // (ADR-0023). Two properties of the Watchtower kind are load-bearing enough to be structural
        // rather than merely enforced in the handlers: it points at a realm and not at a stack, and it is
        // always Public. The second is the invariant "no realm's login host sits behind its own gate",
        // which used to be a force-unprotect in the site projection and is now something the database
        // will not store.
        b.ToTable("routes", t => t.HasCheckConstraint(
            "ck_routes_target",
            """
            ("target" = 'Watchtower' AND "stack_id" IS NULL AND "realm_id" IS NOT NULL AND "access_mode" = 'Public')
            OR ("target" = 'Service' AND "stack_id" IS NOT NULL AND "realm_id" IS NULL)
            """));
        b.HasKey(x => x.Id);
        b.UseXminAsConcurrencyToken();
        b.Property(x => x.Domain).IsRequired();
        b.Property(x => x.ServiceName).IsRequired();
        // Stored as the enum name ("Service"/"Watchtower"); "Service" is the default, which is what the
        // check constraint above reads for a route written without one.
        b.Property(x => x.Target).HasConversion<string>().HasDefaultValue(RouteTarget.Service);
        // Stored as the enum name (e.g. "Active"/"Managed"); the API maps Status to lowercase for the client.
        b.Property(x => x.Status).HasConversion<string>();
        b.Property(x => x.Kind).HasConversion<string>();
        // Stored as the enum name (e.g. "Public"); "Public" is the default so a route written without one
        // is Public rather than rejected. Declared on the model, not only in a migration, so the default is
        // a property of the schema every environment scaffolds rather than of one migration's DDL.
        b.Property(x => x.AccessMode).HasConversion<string>().HasDefaultValue(AccessMode.Public);
        // Stored as the enum name (e.g. "None"); "None" is the default so existing routes forward JWT only.
        b.Property(x => x.IdentityHeaderMode).HasConversion<string>().HasDefaultValue(IdentityHeaderMode.None);
        // Global, and staying that way: a domain is a global resource — DNS and the proxy's site blocks
        // have no notion of realms, so two realms claiming one host could not both be served. A *service*
        // route's realm is inherited from its stack's template rather than stored here (design.md §13);
        // realm_id is filled only by a Watchtower route, which has no stack to inherit from.
        b.HasIndex(x => x.Domain).IsUnique();
        b.HasIndex(x => x.StackId);
        b.HasIndex(x => x.RealmId);
        b.HasOne(x => x.Stack)
            .WithMany()
            .HasForeignKey(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
        // Restrict, unlike the stack end: deleting a realm that still serves Watchtower on a hostname
        // would silently un-serve that hostname (and orphan whatever redirects to it), so realms.delete
        // refuses first and the foreign key is the backstop.
        b.HasOne(x => x.Realm)
            .WithMany()
            .HasForeignKey(x => x.RealmId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

[EntityConfiguration]
public sealed class UserConfiguration : IEntityTypeConfiguration<User> {
    public void Configure(EntityTypeBuilder<User> b) {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        // No xmin token here, unlike the other editable entities: users already have one in
        // ConcurrencyStamp, and a second would break the pattern WatchtowerUserStore is built around —
        // read detached, mutate, attach, write back. A detached read carries no shadow xmin, so every
        // such write would fail as a phantom conflict. ConcurrencyStamp travels on the entity itself and
        // survives the round trip, which is exactly why Identity models it as a column.
        b.Property(x => x.UserName).IsRequired();
        b.Property(x => x.NormalizedUserName).IsRequired();
        b.Property(x => x.PasswordHash).IsRequired();
        b.Property(x => x.SecurityStamp).IsRequired();
        // Optimistic concurrency: the stamp read into memory must still be the stored one at write time.
        b.Property(x => x.ConcurrencyStamp).IsRequired().IsConcurrencyToken();
        // Every login looks the user up by the normalized name *within its realm*, which is also where
        // uniqueness is enforced: a realm is a credential space of its own (design.md §13), so two
        // populations may each have an `admin`, and neither can see the other's. The realm comes first so
        // the index also serves "the accounts of this realm".
        b.HasIndex(x => new { x.RealmId, x.NormalizedUserName }).IsUnique();
        // Restrict: an account may not outlive its population, and deleting a realm out from under its
        // users would leave sessions pointing at accounts nobody can administer.
        b.HasOne(x => x.Realm)
            .WithMany()
            .HasForeignKey(x => x.RealmId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

[EntityConfiguration]
public sealed class UserRecoveryCodeConfiguration : IEntityTypeConfiguration<UserRecoveryCode> {
    public void Configure(EntityTypeBuilder<UserRecoveryCode> b) {
        b.ToTable("user_recovery_codes");
        b.HasKey(x => x.Id);
        b.Property(x => x.CodeHash).IsRequired();
        // Redemption is a point-read on (owner, hash) and generation replaces the whole set for one owner,
        // so the account comes first. Unique because a code is a credential: two rows with the same hash
        // for one account would make a single code redeemable twice, which is the one thing it must not be.
        b.HasIndex(x => new { x.UserId, x.CodeHash }).IsUnique();
        // The codes are part of the account, not a record of it: deleting the account deletes them.
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession> {
    public void Configure(EntityTypeBuilder<AuthSession> b) {
        b.ToTable("auth_sessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).IsRequired();
        // Stored as the enum name (e.g. "Sso").
        b.Property(x => x.Kind).HasConversion<string>();
        b.HasIndex(x => x.TokenHash).IsUnique();
        // Expired sessions are swept in bulk, same as login codes (design.md §4).
        b.HasIndex(x => x.ExpiresAt);
        // Deleting a user or a route revokes the sessions that depend on it.
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Route)
            .WithMany()
            .HasForeignKey(x => x.RouteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class LoginCodeConfiguration : IEntityTypeConfiguration<LoginCode> {
    public void Configure(EntityTypeBuilder<LoginCode> b) {
        b.ToTable("login_codes");
        b.HasKey(x => x.Id);
        b.Property(x => x.CodeHash).IsRequired();
        b.Property(x => x.RedirectUri).IsRequired();
        b.HasIndex(x => x.CodeHash).IsUnique();
        // Codes are short-lived; the sweep of expired rows scans this index.
        b.HasIndex(x => x.ExpiresAt);
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Route)
            .WithMany()
            .HasForeignKey(x => x.RouteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class GroupConfiguration : IEntityTypeConfiguration<Group> {
    public void Configure(EntityTypeBuilder<Group> b) {
        b.ToTable("groups");
        b.HasKey(x => x.Id);
        b.UseXminAsConcurrencyToken();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.NormalizedName).IsRequired();
        // Same shape as users: uniqueness lives on the normalized column, and every lookup that has to be
        // case-blind goes through this index — scoped to the realm, because a group belongs to exactly one
        // population (design.md §13).
        b.HasIndex(x => new { x.RealmId, x.NormalizedName }).IsUnique();
        // Restrict, as for users: a realm with groups still in it is not deletable.
        b.HasOne(x => x.Realm)
            .WithMany()
            .HasForeignKey(x => x.RealmId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

[EntityConfiguration]
public sealed class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember> {
    public void Configure(EntityTypeBuilder<GroupMember> b) {
        b.ToTable("group_members");
        b.HasKey(x => x.Id);
        // One row per (group, user): re-adding a member is idempotent rather than duplicated, which also
        // keeps the membership subquery in RouteAccessPolicy an existence check over an index.
        b.HasIndex(x => new { x.GroupId, x.UserId }).IsUnique();
        // Both ends cascade — a membership outliving either side would be a grant nobody can see.
        b.HasOne(x => x.Group)
            .WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class RouteAccessGrantConfiguration : IEntityTypeConfiguration<RouteAccessGrant> {
    public void Configure(EntityTypeBuilder<RouteAccessGrant> b) {
        // A grant names exactly one subject. Enforced in the schema, not only in the handlers: a row with
        // neither column set grants nobody, and one with both has two readings — and this table is what
        // the verify path consults on every proxied request.
        b.ToTable("route_access_grants", t => t.HasCheckConstraint(
            "ck_route_access_grants_subject",
            "(\"user_id\" IS NOT NULL) <> (\"group_id\" IS NOT NULL)"));
        b.HasKey(x => x.Id);
        // One grant per (route, subject) — re-granting is idempotent rather than duplicated. Two partial
        // indexes rather than one composite: the pair is unique *within* a subject kind, and the rows of
        // the other kind must not be dragged into the constraint. PostgreSQL already treats NULLs as
        // distinct (see the Stack.AppApiToken note above), but the filter states the intent rather than
        // relying on it.
        b.HasIndex(x => new { x.RouteId, x.UserId }).IsUnique()
            .HasFilter("\"user_id\" IS NOT NULL");
        b.HasIndex(x => new { x.RouteId, x.GroupId }).IsUnique()
            .HasFilter("\"group_id\" IS NOT NULL");
        b.HasOne(x => x.Route)
            .WithMany()
            .HasForeignKey(x => x.RouteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        // Deleting a group revokes every grant that named it, the same way deleting a user does.
        b.HasOne(x => x.Group)
            .WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class TemplateManagementGrantConfiguration : IEntityTypeConfiguration<TemplateManagementGrant> {
    public void Configure(EntityTypeBuilder<TemplateManagementGrant> b) {
        b.ToTable("template_management_grants");
        b.HasKey(x => x.Id);
        // One grant per (stack, template) — re-granting updates the existing row rather than adding a
        // second one, so a revoke can never leave a forgotten duplicate behind still granting access.
        b.HasIndex(x => new { x.StackId, x.TemplateId }).IsUnique();
        // Both ends cascade: a grant is meaningless once either the grantee stack or the managed
        // template is gone, and leaving the row behind would re-grant a recycled id.
        b.HasOne(x => x.Stack)
            .WithMany()
            .HasForeignKey(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Template)
            .WithMany()
            .HasForeignKey(x => x.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent> {
    public void Configure(EntityTypeBuilder<AuditEvent> b) {
        b.ToTable("audit_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Category).IsRequired();
        b.Property(x => x.Action).IsRequired();
        b.Property(x => x.Target).IsRequired();
        // The audit view reads newest-first, optionally narrowed by category prefix.
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => x.Category);
    }
}

[EntityConfiguration]
public sealed class MetricHostSampleConfiguration : IEntityTypeConfiguration<MetricHostSample> {
    public void Configure(EntityTypeBuilder<MetricHostSample> b) {
        b.ToTable("metric_host_samples");
        b.HasKey(x => x.Id);
        // Every read is "one tier over a time range"; unique so a re-run flush/rollup upserts by
        // conflict rather than duplicating a bucket.
        b.HasIndex(x => new { x.TierSeconds, x.TUnixSeconds }).IsUnique();
    }
}

[EntityConfiguration]
public sealed class MetricContainerSampleConfiguration : IEntityTypeConfiguration<MetricContainerSample> {
    public void Configure(EntityTypeBuilder<MetricContainerSample> b) {
        b.ToTable("metric_container_samples");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContainerName).IsRequired();
        // Same shape as the host index, plus the series identity. The retention delete scans the
        // (tier, t) prefix; history reads scan the same prefix and group by name in memory.
        b.HasIndex(x => new { x.TierSeconds, x.TUnixSeconds, x.ContainerName }).IsUnique();
    }
}

[EntityConfiguration]
public sealed class StackUpdateCheckConfiguration : IEntityTypeConfiguration<StackUpdateCheck> {
    public void Configure(EntityTypeBuilder<StackUpdateCheck> b) {
        b.ToTable("stack_update_checks");
        b.HasKey(x => x.StackId);
        // Persist the image list as newline-separated text (avoids a JSON column dependency).
        var comparer = new ValueComparer<string[]>(
            (a, c) => a!.SequenceEqual(c!),
            v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToArray());
        b.Property(x => x.OutdatedImages)
            .HasConversion(
                v => string.Join('\n', v),
                v => v.Length == 0 ? Array.Empty<string>() : v.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                comparer);
        // The remote digest behind each outdated image, same newline-separated text, one
        // "<image> <digest>" pair per line — an image reference never contains whitespace.
        var digestComparer = new ValueComparer<Dictionary<string, string>>(
            (a, c) => SameDigests(a!, c!),
            // XORed, not folded in sequence: equality above ignores both order and key casing, so the
            // hash has to as well, or two equal maps can hash apart. (Sum would overflow-check.)
            v => v.Aggregate(0, (h, kv) => h ^ HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(kv.Key), kv.Value.GetHashCode())),
            v => new Dictionary<string, string>(v, StringComparer.OrdinalIgnoreCase));
        b.Property(x => x.OutdatedImageDigests)
            .HasConversion(v => FormatDigests(v), v => ParseDigests(v), digestComparer);
        // Same newline-separated text as OutdatedImages, and deliberately the same shape: it is the
        // Releases-mode counterpart of that list (a container name never contains a newline).
        b.Property(x => x.DriftedContainers)
            .HasConversion(
                v => string.Join('\n', v),
                v => v.Length == 0 ? Array.Empty<string>() : v.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                comparer);
        b.HasOne(x => x.Stack)
            .WithOne(s => s.UpdateCheck)
            .HasForeignKey<StackUpdateCheck>(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static bool SameDigests(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var digest) && digest == kv.Value);

    internal static string FormatDigests(Dictionary<string, string> digests) =>
        string.Join('\n', digests.Select(kv => $"{kv.Key} {kv.Value}"));

    internal static Dictionary<string, string> ParseDigests(string value) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var separator = line.IndexOf(' ');
            if (separator <= 0 || separator == line.Length - 1) continue;
            map[line[..separator]] = line[(separator + 1)..];
        }
        return map;
    }
}

// ── The proxy/auth plane's state (ADR-0024 decision 4) ───────────────────────
// Four tables that used to be files on the data volume. What they have in common is that every
// instance has to be able to read them — the SNI map, the challenge answers and the signing key are
// all things a request lands on whichever node the load balancer picked.

[EntityConfiguration]
public sealed class ProxyCertificateConfiguration : IEntityTypeConfiguration<ProxyCertificate> {
    public void Configure(EntityTypeBuilder<ProxyCertificate> b) {
        b.ToTable("proxy_certificates");
        b.HasKey(x => x.Id);
        // Two instances can finish an order for the same host at the same moment (the issuer lease makes
        // that unlikely, not impossible — a lease handover mid-order is exactly the window). The upsert
        // in CertificateStore.InstallAsync resolves it, and the concurrency token is what makes the
        // loser's write a Conflict rather than a silent overwrite of the newer certificate.
        b.UseXminAsConcurrencyToken();
        b.Property(x => x.Host).IsRequired();
        // Lowercased by every writer, so a plain unique index is the whole rule: one certificate per SNI
        // name, and the store's lookup is an exact match on the same normalized form.
        b.HasIndex(x => x.Host).IsUnique();
        b.Property(x => x.CertificatePem).IsRequired();
        b.Property(x => x.PrivateKey).IsRequired();
        b.Property(x => x.Protection).IsRequired();
        b.Property(x => x.Issuer).IsRequired();
        b.Property(x => x.Thumbprint).IsRequired();
        b.Property(x => x.Source).IsRequired();
    }
}

[EntityConfiguration]
public sealed class AcmeAccountConfiguration : IEntityTypeConfiguration<AcmeAccount> {
    public void Configure(EntityTypeBuilder<AcmeAccount> b) {
        b.ToTable("acme_accounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.DirectoryUrl).IsRequired();
        // The unique index is load-bearing rather than descriptive: it is what makes the
        // INSERT … ON CONFLICT DO NOTHING in AcmeAccountStore a race guard, so two instances starting
        // together register one account with the CA instead of two.
        b.HasIndex(x => x.DirectoryUrl).IsUnique();
        b.Property(x => x.PrivateKey).IsRequired();
        b.Property(x => x.Protection).IsRequired();
    }
}

[EntityConfiguration]
public sealed class AcmeHttpChallengeConfiguration : IEntityTypeConfiguration<AcmeHttpChallenge> {
    public void Configure(EntityTypeBuilder<AcmeHttpChallenge> b) {
        b.ToTable("acme_http_challenges");
        // The token is the key: the middleware's only query is "is this exact token answerable", on a
        // path the open internet can reach, so it should be one index seek and nothing else.
        b.HasKey(x => x.Token);
        b.Property(x => x.KeyAuthorization).IsRequired();
        b.Property(x => x.Host).IsRequired();
        // The sweep in the certificate manager's pass deletes by expiry; without this it would seq-scan
        // a table whose rows are otherwise only ever fetched by primary key.
        b.HasIndex(x => x.ExpiresAt);
    }
}

[EntityConfiguration]
public sealed class SigningKeyConfiguration : IEntityTypeConfiguration<SigningKey> {
    public void Configure(EntityTypeBuilder<SigningKey> b) {
        b.ToTable("signing_keys");
        // Keyed by purpose, so "there is exactly one identity-assertion key" is a schema fact rather
        // than a convention the create path has to remember.
        b.HasKey(x => x.Purpose);
        b.Property(x => x.PrivateKey).IsRequired();
        b.Property(x => x.Protection).IsRequired();
        b.Property(x => x.KeyId).IsRequired();
    }
}

/// <summary>
/// PostgreSQL's <c>xmin</c> system column as an EF optimistic-concurrency token (ADR-0024 decision 3).
/// </summary>
/// <remarks>
/// A shadow property rather than one on the entity: <c>xmin</c> is the database's own bookkeeping —
/// the id of the transaction that last wrote the row — so no application code should be able to read
/// or set it. Npgsql's <c>UseXminAsConcurrencyToken</c> shorthand was removed in the 9.x provider;
/// this is the mapping it used to emit, in one place so the four entities that want it cannot drift.
/// </remarks>
internal static class XminConcurrency {
    public static EntityTypeBuilder<T> UseXminAsConcurrencyToken<T>(this EntityTypeBuilder<T> builder)
        where T : class {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
        return builder;
    }
}
