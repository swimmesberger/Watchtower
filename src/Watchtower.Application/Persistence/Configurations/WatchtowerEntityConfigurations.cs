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
        b.HasIndex(x => x.Name).IsUnique();
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
        b.HasIndex(x => x.Name).IsUnique();
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
        // Every login looks the user up by the normalized name, which is also where uniqueness is enforced.
        b.HasIndex(x => x.NormalizedUserName).IsUnique();
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
public sealed class RouteAccessGrantConfiguration : IEntityTypeConfiguration<RouteAccessGrant> {
    public void Configure(EntityTypeBuilder<RouteAccessGrant> b) {
        b.ToTable("route_access_grants");
        b.HasKey(x => x.Id);
        // One grant per (route, user) — re-granting is idempotent rather than duplicated.
        b.HasIndex(x => new { x.RouteId, x.UserId }).IsUnique();
        b.HasOne(x => x.Route)
            .WithMany()
            .HasForeignKey(x => x.RouteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

[EntityConfiguration]
public sealed class AuthEventConfiguration : IEntityTypeConfiguration<AuthEvent> {
    public void Configure(EntityTypeBuilder<AuthEvent> b) {
        b.ToTable("auth_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).IsRequired();
        // The audit view reads newest-first.
        b.HasIndex(x => x.CreatedAt);
        // The trail survives the subjects it mentions: detach instead of cascading.
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Route)
            .WithMany()
            .HasForeignKey(x => x.RouteId)
            .OnDelete(DeleteBehavior.SetNull);
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
        b.HasOne(x => x.Stack)
            .WithOne(s => s.UpdateCheck)
            .HasForeignKey<StackUpdateCheck>(x => x.StackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
