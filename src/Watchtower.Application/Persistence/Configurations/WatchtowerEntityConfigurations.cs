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
