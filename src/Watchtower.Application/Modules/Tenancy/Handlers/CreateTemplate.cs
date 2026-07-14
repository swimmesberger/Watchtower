using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Creates a stack template. Base environment variables (if any) are set atomically.</summary>
[Handler("templates.create")]
public sealed class CreateTemplate(WatchtowerDbContext db)
    : IHandler<CreateTemplate.Command, Result<CreateTemplate.Response>> {
    public sealed record Command(
        string Name,
        string RepositoryUrl,
        string ComposeFilePath,
        string Branch,
        int? CredentialId,
        string DomainPattern,
        string TargetServiceName,
        int TargetPort,
        IReadOnlyList<TemplateEnvVarInput>? BaseEnvVars);

    public sealed record Response(StackTemplateDto Template);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(command.Name))
            return AppError.Validation("Template name is required.");
        if (!command.DomainPattern.Contains("{tenant}"))
            return AppError.Validation("Domain pattern must contain the {tenant} placeholder.");
        if (command.TargetPort is < 1 or > 65535)
            return AppError.Validation("Target port must be between 1 and 65535.");
        if (command.BaseEnvVars is { Count: > 0 } && TenancyMapping.FirstDuplicateKey(command.BaseEnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");
        if (await db.StackTemplates.AnyAsync(t => t.Name == command.Name, ct))
            return AppError.Validation($"A template named '{command.Name}' already exists.");

        var template = new StackTemplate {
            Name = command.Name,
            RepositoryUrl = command.RepositoryUrl,
            ComposeFilePath = command.ComposeFilePath,
            Branch = command.Branch,
            CredentialId = command.CredentialId,
            DomainPattern = command.DomainPattern.Trim(),
            TargetServiceName = command.TargetServiceName.Trim(),
            TargetPort = command.TargetPort,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.StackTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        if (command.BaseEnvVars is { Count: > 0 }) {
            foreach (var v in command.BaseEnvVars)
                db.StackTemplateEnvVars.Add(new StackTemplateEnvVar { TemplateId = template.Id, Key = v.Key, Value = v.Value });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        return new Response(TenancyMapping.ToDto(template, instanceCount: 0));
    }
}
