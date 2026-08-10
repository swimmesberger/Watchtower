using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Groups.Handlers;

/// <summary>
/// Creates an empty group. Members are added separately (<see cref="SetGroupMembers"/>), so creating one
/// grants nobody anything by itself.
/// </summary>
/// <remarks>
/// The duplicate check is a read followed by an insert, and the unique index on
/// <c>groups.normalized_name</c> is what actually settles a race between two administrators — the check
/// exists to turn the common case into a clear <c>Conflict</c> rather than a database exception.
/// </remarks>
[Handler("groups.create")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class CreateGroup(WatchtowerDbContext db, ICurrentUser currentUser, TimeProvider time)
    : IHandler<CreateGroup.Command, Result<CreateGroup.Response>> {

    /// <summary>
    /// <paramref name="RealmId"/> is optional and last (a default value is what marks a parameter
    /// non-required in the generated schema): a client that predates realms omits it and creates an
    /// operator group, exactly as it always did.
    /// </summary>
    public sealed record Command(string Name, int? RealmId = null);

    public sealed record Response(GroupDto Group);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (GroupMapping.ValidateName(command.Name, out var name) is { } invalid)
            return invalid;

        var realmId = command.RealmId ?? Realm.SystemRealmId;
        if (await GroupMapping.FindRealmAsync(db, realmId, ct) is null)
            return AppError.Validation($"No realm exists with id {realmId}.");

        var normalized = GroupMapping.Normalize(name);
        // Scoped to the realm, like the unique index behind it: two populations may each have a "staff".
        if (await db.Groups.AsNoTracking()
                .AnyAsync(g => g.RealmId == realmId && g.NormalizedName == normalized, ct)) {
            return AppError.Conflict($"A group named '{name}' already exists in that realm.");
        }

        var group = new Group {
            RealmId = realmId,
            Name = name,
            NormalizedName = normalized,
            CreatedAt = time.GetUtcNow(),
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);

        // Past the commit point: the group exists, so the trail is written uncancellably.
        await GroupMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.GroupCreated, group.Id, group.Name, $"realmId={realmId}");

        return new Response(GroupMapping.ToDto(group, memberCount: 0));
    }
}
