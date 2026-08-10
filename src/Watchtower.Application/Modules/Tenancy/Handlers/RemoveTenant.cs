using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Deprovisions one tenant: stops and removes its containers, deletes its stack (cascading routes, env
/// vars and deploy events), and reloads the proxy so its domain stops being served.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated-only, like <c>templates.addTenant</c> and <c>stacks.delete</c> — creating and removing
/// tenants is stack administration, not privilege management, so it is not restricted to the Admin role
/// the grant handlers carry.
/// </para>
/// <para>
/// The ordering rules — refuse under an active deploy, bring compose down before the row is deleted,
/// abort the whole thing if it can't — live in <see cref="TenantTeardownService"/>, which the public
/// management API's <c>DELETE</c> shares.
/// </para>
/// </remarks>
[Handler("templates.removeTenant")]
public sealed class RemoveTenant(TenantTeardownService teardown)
    : IHandler<RemoveTenant.Command, Result<RemoveTenant.Response>> {
    /// <summary>Which tenant to remove, and whether to destroy its data with it.</summary>
    /// <param name="TemplateId">Template the tenant belongs to.</param>
    /// <param name="Slug">Tenant slug within that template.</param>
    /// <param name="RemoveVolumes">When true the tenant's named volumes are deleted too — irreversible.</param>
    public sealed record Command(int TemplateId, string Slug, bool RemoveVolumes);

    /// <summary>The removed tenant.</summary>
    /// <param name="Slug">Slug of the tenant that was removed.</param>
    public sealed record Response(string Slug);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var result = await teardown.TeardownAsync(command.TemplateId, command.Slug, command.RemoveVolumes, ct);
        return result.Status switch {
            // The normalized slug, not the one the caller typed: it is the identifier that was stored.
            TenantTeardownStatus.Removed => new Response(result.Slug!),
            TenantTeardownStatus.TenantNotFound => AppError.NotFound(result.Error!),
            TenantTeardownStatus.DeployActive => AppError.Conflict(result.Error!),
            // Compose could not stop the containers, so nothing was deleted: an infrastructure failure
            // the operator retries, not a malformed request.
            _ => AppError.Internal(result.Error!),
        };
    }
}
