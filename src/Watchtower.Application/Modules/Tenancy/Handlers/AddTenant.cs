using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Creates a tenant from a template: a new isolated stack (compose project = the slug-derived name),
/// the merged env vars, a managed route derived from the template's domain pattern, and an initial
/// deploy. The route's service container joins the edge network on the first successful deploy.
/// </summary>
/// <remarks>
/// A thin wrapper over <see cref="TenantProvisioningService"/>, which the public management API's
/// tenant-creation endpoint shares, so both surfaces provision through one code path. Only the error
/// projection differs: a collision is a <c>Validation</c> failure here — the shape this method has
/// always returned, and what the operator UI renders — while the REST surface maps the same outcome
/// onto <c>409</c>.
/// </remarks>
[Handler("templates.addTenant")]
public sealed class AddTenant(TenantProvisioningService provisioning)
    : IHandler<AddTenant.Command, Result<AddTenant.Response>> {
    public sealed record Command(int TemplateId, string Slug, IReadOnlyList<TemplateEnvVarInput>? EnvOverrides);
    public sealed record Response(TenantDto Tenant);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var result = await provisioning.ProvisionAsync(command.TemplateId, command.Slug, command.EnvOverrides, ct);
        return result.Status switch {
            TenantProvisionStatus.Created => new Response(result.Tenant!),
            TenantProvisionStatus.TemplateNotFound => AppError.NotFound(result.Error!),
            _ => AppError.Validation(result.Error!),
        };
    }
}
