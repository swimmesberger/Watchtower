using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Updates a template. When BaseEnvVars is provided the base env set is replaced atomically.</summary>
[Handler("templates.update")]
public sealed class UpdateTemplate(WatchtowerDbContext db)
    : IHandler<UpdateTemplate.Command, Result<UpdateTemplate.Response>> {
    public sealed record Command(
        int Id,
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
        if (!command.DomainPattern.Contains("{tenant}"))
            return AppError.Validation("Domain pattern must contain the {tenant} placeholder.");
        if (command.TargetPort is < 1 or > 65535)
            return AppError.Validation("Target port must be between 1 and 65535.");
        if (command.BaseEnvVars is { Count: > 0 } && TenancyMapping.FirstDuplicateKey(command.BaseEnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");

        var template = await db.StackTemplates.FirstOrDefaultAsync(t => t.Id == command.Id, ct);
        if (template is null)
            return AppError.NotFound($"Template {command.Id} not found");
        if (await db.StackTemplates.AnyAsync(t => t.Name == command.Name && t.Id != command.Id, ct))
            return AppError.Validation($"A template named '{command.Name}' already exists.");

        template.Name = command.Name;
        template.RepositoryUrl = command.RepositoryUrl;
        template.ComposeFilePath = command.ComposeFilePath;
        template.Branch = command.Branch;
        template.CredentialId = command.CredentialId;
        template.DomainPattern = command.DomainPattern.Trim();
        template.TargetServiceName = command.TargetServiceName.Trim();
        template.TargetPort = command.TargetPort;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (command.BaseEnvVars is not null) {
            await db.StackTemplateEnvVars.Where(v => v.TemplateId == template.Id).ExecuteDeleteAsync(ct);
            foreach (var v in command.BaseEnvVars)
                db.StackTemplateEnvVars.Add(new StackTemplateEnvVar { TemplateId = template.Id, Key = v.Key, Value = v.Value });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var count = await db.Stacks.CountAsync(s => s.TemplateId == template.Id, ct);
        return new Response(TenancyMapping.ToDto(template, count));
    }
}
