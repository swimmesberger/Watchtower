using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Persistence.Configurations;

/// <summary>
/// EF Core model configuration for every Watchtower entity. Each class is discovered by the
/// Elarion EF generator via <c>[EntityConfiguration]</c> and applied by the generated
/// <c>ConfigureEntities</c> method on <see cref="WatchtowerDbContext"/>. Column names are
/// snake_cased by convention (<c>UseSnakeCaseNamingConvention</c>); table names are set explicitly.
/// </summary>
[EntityConfiguration]
public sealed class RealmConfiguration : IEntityTypeConfiguration<Realm> {
    public void Configure(EntityTypeBuilder<Realm> b) {
        b.ToTable("realms");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Slug).IsRequired();
        // The slug is the realm's identity on the wire (the `realm` JWT claim), so it is unique and the
        // handlers refuse to change it.
        b.HasIndex(x => x.Slug).IsUnique();
        // At most one realm may own a given login host: the host decides which population a visitor is
        // authenticating into, so two realms sharing one would make that ambiguous. Filtered rather than
        // plain unique because "no host yet" is a legitimate state for any number of realms — SQLite
        // already treats NULLs as distinct, but the filter states the intent (the RouteAccessGrant
        // precedent) and is what a future Postgres backend would need anyway.
        b.HasIndex(x => x.AuthHost).IsUnique().HasFilter("\"auth_host\" IS NOT NULL");
    }
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
public sealed class StackConfiguration : IEntityTypeConfiguration<Stack> {
    public void Configure(EntityTypeBuilder<Stack> b) {
        b.ToTable("stacks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.RepositoryUrl).IsRequired();
        b.Property(x => x.ComposeFilePath).IsRequired();
        b.Property(x => x.Branch).IsRequired();
        b.Property(x => x.ComposeProjectName).IsRequired();
        // Stored as the enum name (e.g. "Success"); the API maps it to lowercase for the client.
        b.Property(x => x.LastDeployStatus).HasConversion<string>();
        // Stored as the enum name (e.g. "OnChange"); the API maps it to camelCase for the client.
        b.Property(x => x.AutoDeployMode).HasConversion<string>();
        // Stored as the enum name ("Stop"/"Pause"); the API maps it to lowercase. The column default
        // is what the migration backfills existing rows with, so a stack from before quiesce modes
        // existed keeps stopping its containers, exactly as before.
        b.Property(x => x.BackupQuiesceMode).HasConversion<string>().HasDefaultValue(BackupQuiesceMode.Stop);
        b.HasIndex(x => x.Name).IsUnique();
        // The App API authenticates every request by looking the presented bearer token up here, so
        // the column must be indexed. Unique guards against two stacks ever sharing a token; SQLite
        // treats NULLs as distinct, so any number of stacks may still have no token yet.
        b.HasIndex(x => x.AppApiToken).IsUnique();
        b.HasOne(x => x.Credential)
            .WithMany()
            .HasForeignKey(x => x.CredentialId)
            .OnDelete(DeleteBehavior.SetNull);
        // Tenant instances link back to their template; deleting a template detaches (not deletes) them.
        // (TemplateId, TenantSlug) is unique — SQLite treats NULLs as distinct, so standalone stacks
        // (both null) never collide.
        b.HasOne(x => x.Template)
            .WithMany(t => t.Instances)
            .HasForeignKey(x => x.TemplateId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.TemplateId, x.TenantSlug }).IsUnique();
    }
}

[EntityConfiguration]
public sealed class StackTemplateConfiguration : IEntityTypeConfiguration<StackTemplate> {
    public void Configure(EntityTypeBuilder<StackTemplate> b) {
        b.ToTable("stack_templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.RepositoryUrl).IsRequired();
        b.Property(x => x.ComposeFilePath).IsRequired();
        b.Property(x => x.Branch).IsRequired();
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
        b.HasOne(x => x.Credential)
            .WithMany()
            .HasForeignKey(x => x.CredentialId)
            .OnDelete(DeleteBehavior.SetNull);
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
        b.ToTable("routes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Domain).IsRequired();
        b.Property(x => x.ServiceName).IsRequired();
        // Stored as the enum name (e.g. "Active"/"Managed"); the API maps Status to lowercase for the client.
        b.Property(x => x.Status).HasConversion<string>();
        b.Property(x => x.Kind).HasConversion<string>();
        // Stored as the enum name (e.g. "Public"); "Public" is the default so existing routes keep today's behaviour.
        b.Property(x => x.AccessMode).HasConversion<string>();
        // Stored as the enum name (e.g. "None"); "None" is the default so existing routes forward JWT only.
        b.Property(x => x.IdentityHeaderMode).HasConversion<string>();
        // Global, and staying that way: a domain is a global resource — DNS and the proxy's site blocks
        // have no notion of realms, so two realms claiming one host could not both be served. A route's
        // realm is inherited from its stack's template rather than stored here (design.md §13).
        b.HasIndex(x => x.Domain).IsUnique();
        b.HasIndex(x => x.StackId);
        b.HasOne(x => x.Stack)
            .WithMany()
            .HasForeignKey(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class UserConfiguration : IEntityTypeConfiguration<User> {
    public void Configure(EntityTypeBuilder<User> b) {
        b.ToTable("users");
        b.HasKey(x => x.Id);
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
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.NormalizedName).IsRequired();
        // Same shape as users: uniqueness lives on the normalized column so names are case-insensitive
        // on SQLite too, and every lookup that has to be case-blind goes through this index — scoped to
        // the realm, because a group belongs to exactly one population (design.md §13).
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
        // the other kind must not be dragged into the constraint. SQLite already treats NULLs as distinct
        // (see the Stack.AppApiToken note above), but the filter states the intent rather than relying on
        // it, and is what a future Postgres backend would need anyway.
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
