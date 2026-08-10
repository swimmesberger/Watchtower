using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Realms.Handlers;

/// <summary>
/// Removes an empty realm. Deliberately the only thing it can remove: a realm still holding accounts,
/// groups or categories is refused rather than cascaded.
/// </summary>
/// <remarks>
/// No cascades, and the foreign keys say so too (<c>DeleteBehavior.Restrict</c> on all three). Deleting a
/// population is not an operation whose blast radius should be discovered afterwards — it would take every
/// account's credentials, every group's grants, and every category's tenant stacks with it in one call.
/// Emptying the realm first is the same amount of work made visible, and each step is separately auditable.
/// <para>
/// The system realm is never deletable: it is the operator population, the realm every pre-realm row was
/// backfilled into, and the fallback every unrecognised host resolves to. Removing it would leave an
/// instance nobody can administer and no login page to fix it from.
/// </para>
/// </remarks>
[Handler("realms.delete")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class DeleteRealm(WatchtowerDbContext db, ICurrentUser currentUser, TimeProvider time)
    : IHandler<DeleteRealm.Command, Result<DeleteRealm.Response>> {

    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (realm is null)
            return AppError.NotFound($"Realm {command.Id} not found.");

        if (realm.IsSystem)
            return AppError.Conflict("The operator realm is built in and cannot be deleted.");

        var userCount = await db.Users.CountAsync(u => u.RealmId == realm.Id, ct);
        var groupCount = await db.Groups.CountAsync(g => g.RealmId == realm.Id, ct);
        var templateCount = await db.StackTemplates.CountAsync(t => t.RealmId == realm.Id, ct);
        if (userCount + groupCount + templateCount > 0) {
            return AppError.Conflict(
                $"Realm '{realm.Slug}' still holds {userCount} account(s), {groupCount} group(s) and " +
                $"{templateCount} template(s). Remove them first.");
        }

        var slug = realm.Slug;
        db.Realms.Remove(realm);
        await db.SaveChangesAsync(ct);

        // Past the commit point, and delete-then-audit like the Users and Groups handlers: a delete that
        // fails must not leave a trail claiming it happened.
        await RealmMapping.RecordAsync(db, currentUser, time, AuthEventKinds.RealmDeleted, command.Id, slug);

        return new Response(command.Id);
    }
}
