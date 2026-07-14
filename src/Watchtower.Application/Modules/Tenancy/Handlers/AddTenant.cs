using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Creates a tenant from a template: a new isolated stack (compose project = the slug-derived name),
/// the merged env vars, a managed route derived from the template's domain pattern, and an initial
/// deploy. The route's service container joins the edge network on the first successful deploy.
/// </summary>
[Handler("templates.addTenant")]
public sealed class AddTenant(WatchtowerDbContext db, DeployQueueService deployQueue)
    : IHandler<AddTenant.Command, Result<AddTenant.Response>> {
    public sealed record Command(int TemplateId, string Slug, IReadOnlyList<TemplateEnvVarInput>? EnvOverrides);
    public sealed record Response(TenantDto Tenant);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var slug = TenancyMapping.NormalizeSlug(command.Slug);
        if (slug is null)
            return AppError.Validation("Slug must start with a letter or digit and contain only lowercase letters, digits, and hyphens.");
        if (command.EnvOverrides is { Count: > 0 } && TenancyMapping.FirstDuplicateKey(command.EnvOverrides) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");

        var template = await db.StackTemplates.Include(t => t.BaseEnvVars)
            .FirstOrDefaultAsync(t => t.Id == command.TemplateId, ct);
        if (template is null)
            return AppError.NotFound($"Template {command.TemplateId} not found");

        if (await db.Stacks.AnyAsync(s => s.TemplateId == template.Id && s.TenantSlug == slug, ct))
            return AppError.Validation($"Tenant '{slug}' already exists for this template.");

        var stackName = $"{template.Name}-{slug}";
        if (await db.Stacks.AnyAsync(s => s.Name == stackName, ct))
            return AppError.Validation($"A stack named '{stackName}' already exists.");

        var domain = TenancyMapping.RenderDomain(template.DomainPattern, slug);
        if (await db.Routes.AnyAsync(r => r.Domain == domain, ct))
            return AppError.Validation($"Domain '{domain}' is already routed.");

        var stack = new Stack {
            Name = stackName,
            RepositoryUrl = template.RepositoryUrl,
            ComposeFilePath = template.ComposeFilePath,
            Branch = template.Branch,
            ComposeProjectName = TenancyMapping.ProjectName(template.Name, slug),
            CredentialId = template.CredentialId,
            TemplateId = template.Id,
            TenantSlug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct)) {
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);

            foreach (var v in TenancyMapping.MergeEnv(template.BaseEnvVars, command.EnvOverrides))
                db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = v.Key, Value = v.Value });

            db.Routes.Add(new Route {
                StackId = stack.Id,
                Domain = domain,
                ServiceName = template.TargetServiceName,
                ContainerPort = template.TargetPort,
                TlsEnabled = true,
                IsPrimary = true,
                Kind = DomainKind.Managed,
                Status = RouteStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var enq = deployQueue.Enqueue(stack.Id, "tenant-create");
        return new Response(new TenantDto(stack.Id, slug, stack.Name, domain, enq.Status, null));
    }
}
