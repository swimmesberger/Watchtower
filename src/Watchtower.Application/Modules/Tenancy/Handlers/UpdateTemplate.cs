using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Updates a template. When BaseEnvVars is provided the base env set is replaced atomically.</summary>
/// <remarks>
/// The realm may only be changed while the template has <em>no</em> tenants (docs/central-auth/design.md
/// §13). Moving a populated category would silently re-point every tenant route at another population: the
/// accounts currently using them would stop being admitted on their next request, and the accounts of the
/// new realm would be let in without anybody having granted them anything. Emptying the category first
/// makes that an explicit act rather than a side effect of a form save.
/// </remarks>
[Handler("templates.update")]
public sealed class UpdateTemplate(WatchtowerDbContext db)
    : IHandler<UpdateTemplate.Command, Result<UpdateTemplate.Response>> {
    /// <summary><paramref name="RealmId"/> omitted leaves the category where it is.</summary>
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
        IReadOnlyList<TemplateEnvVarInput>? BaseEnvVars,
        int? RealmId = null);

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

        var tenantCount = await db.Stacks.CountAsync(s => s.TemplateId == template.Id, ct);
        if (command.RealmId is { } realmId && realmId != template.RealmId) {
            if (!await db.Realms.AnyAsync(r => r.Id == realmId, ct))
                return AppError.Validation($"No realm exists with id {realmId}.");
            if (tenantCount > 0) {
                return AppError.Conflict(
                    $"Template '{template.Name}' has {tenantCount} tenant(s), so its realm cannot be " +
                    "changed. Remove them first.");
            }
            template.RealmId = realmId;
        }

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

        // Read before the write above, not after: nothing here changes the tenant count, and the value was
        // already needed to decide whether the realm may move.
        return new Response(TenancyMapping.ToDto(template, tenantCount));
    }
}
