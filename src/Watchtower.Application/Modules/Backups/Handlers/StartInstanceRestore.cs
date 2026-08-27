using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Replaces this Watchtower's database with the one in the uploaded bundle (ADR-0027 §5). The
/// destructive half of the restore: everything this instance currently knows — its stacks, accounts,
/// routes, settings and keys — is replaced by the bundle's, and the containers it deployed keep running
/// unmanaged until the recovery checklist redeploys them.
/// </summary>
/// <remarks>
/// Returns as soon as the coordinator container has been started; Watchtower stops answering a few
/// seconds later and comes back on the restored database, where the caller's session no longer exists.
/// The UI is expected to wait for the restart and send the operator to the login page.
/// </remarks>
[Handler("backups.startInstanceRestore")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class StartInstanceRestore(
    WatchtowerDbContext db,
    InstanceRestoreService restore,
    InstanceRestoreStaging staging,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<StartInstanceRestore.Command, Result<StartInstanceRestore.Response>> {
    public sealed record Command;

    /// <param name="SourceInstance">The instance the bundle came from, for the "restarting" banner.</param>
    public sealed record Response(string SourceInstance);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (staging.Current is not { } staged)
            return AppError.Validation("Upload a backup bundle first.");

        // Nothing that writes may be in flight: a deploy or backup finishing against a database that is
        // being dropped would leave its own event row in a state nothing can explain afterwards.
        if (await db.DeployEvents.AnyAsync(e => e.Status == "running" || e.Status == "queued", ct))
            return AppError.Conflict("A deploy is in progress — restore once it has finished.");
        if (await db.BackupEvents.AnyAsync(
                e => e.Status == BackupStatuses.Running || e.Status == BackupStatuses.Queued, ct))
            return AppError.Conflict("A backup or restore is in progress — restore once it has finished.");

        var actor = await audit.ActorAsync(currentUser, ct);
        try {
            await restore.StartAsync(actor, ct);
        } catch (InvalidOperationException ex) {
            // Everything the restore refuses is a configuration or bundle problem the operator can act
            // on, and none of it has touched the database yet.
            return AppError.Validation(ex.Message);
        }
        return new Response(staged.Manifest.InstanceName);
    }
}
