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
/// <para>
/// <b>The final backup is asynchronous, and the response says so.</b> With <c>finalBackup</c> the
/// teardown is chained onto a backup run instead of happening inline: the backup queue is single-flight
/// process-wide and a tenant's archive can take minutes, so blocking the call until it finished would
/// hang the request behind every other backup on the box. The call therefore answers
/// <c>removed: false</c> with a <c>backupEventId</c>, and the tenant disappears when the backup
/// succeeds. A failed backup <em>aborts</em> the removal — the tenant is still there, and the audit
/// trail says why (<see cref="BackupChainCoordinator"/>).
/// </para>
/// </remarks>
[Handler("templates.removeTenant")]
public sealed class RemoveTenant(TenantTeardownService teardown, BackupQueueService backupQueue)
    : IHandler<RemoveTenant.Command, Result<RemoveTenant.Response>> {
    /// <summary>Which tenant to remove, and whether to destroy its data with it.</summary>
    /// <param name="TemplateId">Template the tenant belongs to.</param>
    /// <param name="Slug">Tenant slug within that template.</param>
    /// <param name="RemoveVolumes">When true the tenant's named volumes are deleted too — irreversible.</param>
    /// <param name="FinalBackup">
    /// Take one last backup and remove the tenant only if it succeeds. The removal becomes asynchronous
    /// — see the remarks.
    /// </param>
    public sealed record Command(int TemplateId, string Slug, bool RemoveVolumes, bool FinalBackup = false);

    /// <summary>The removed tenant.</summary>
    /// <param name="Slug">Slug of the tenant that was removed, or that is being backed up before removal.</param>
    /// <param name="Removed">
    /// False when a final backup was asked for: the tenant is still there and will be removed when the
    /// backup succeeds. Always true otherwise — an unremoved tenant is an error on that path.
    /// </param>
    /// <param name="BackupEventId">The final backup's tracking event, when one was enqueued.</param>
    public sealed record Response(string Slug, bool Removed = true, int? BackupEventId = null);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (command.FinalBackup) {
            // The two knowable refusals are answered now rather than four minutes later: an operator
            // whose slug is wrong, or whose tenant is mid-deploy, gets the same error they always did.
            var (failure, tenant) = await teardown.ResolveAsync(command.TemplateId, command.Slug, ct);
            if (failure is not null) return Refuse(failure);
            var enqueued = backupQueue.Enqueue(
                tenant!.StackId, BackupTriggers.Final,
                BackupChainStep.ForTenantTeardown(
                    tenant.StackId, command.TemplateId, tenant.Slug, command.RemoveVolumes));
            return new Response(tenant.Slug, Removed: false, BackupEventId: enqueued.BackupEventId);
        }

        var result = await teardown.TeardownAsync(command.TemplateId, command.Slug, command.RemoveVolumes, ct);
        return result.Status switch {
            // The normalized slug, not the one the caller typed: it is the identifier that was stored.
            TenantTeardownStatus.Removed => new Response(result.Slug!),
            _ => Refuse(result),
        };
    }

    /// <summary>One mapping from a teardown refusal to an <see cref="AppError"/>, both call paths.</summary>
    private static AppError Refuse(TenantTeardownResult result) => result.Status switch {
        TenantTeardownStatus.TenantNotFound => AppError.NotFound(result.Error!),
        TenantTeardownStatus.DeployActive => AppError.Conflict(result.Error!),
        // Compose could not stop the containers, so nothing was deleted: an infrastructure failure the
        // operator retries, not a malformed request.
        _ => AppError.Internal(result.Error!),
    };
}
