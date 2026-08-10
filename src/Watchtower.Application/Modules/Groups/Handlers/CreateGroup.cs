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

    public sealed record Command(string Name);
    public sealed record Response(GroupDto Group);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (GroupMapping.ValidateName(command.Name, out var name) is { } invalid)
            return invalid;

        var normalized = GroupMapping.Normalize(name);
        if (await db.Groups.AsNoTracking().AnyAsync(g => g.NormalizedName == normalized, ct))
            return AppError.Conflict($"A group named '{name}' already exists.");

        var group = new Group {
            Name = name,
            NormalizedName = normalized,
            CreatedAt = time.GetUtcNow(),
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);

        // Past the commit point: the group exists, so the trail is written uncancellably.
        await GroupMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.GroupCreated, group.Id, group.Name);

        return new Response(GroupMapping.ToDto(group, memberCount: 0));
    }
}
