using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Groups.Handlers;

/// <summary>
/// Renames a group. Memberships and route grants reference it by id, so they are untouched.
/// </summary>
/// <remarks>
/// What a rename <em>does</em> change is what protected upstreams see: the name travels in the forwarded
/// group header and the JWT's <c>groups</c> claim, so an application that maps group names onto its own
/// roles stops recognising the old one from the next verified request. That is the intended semantics —
/// the name is the contract with the upstream — but it is why the same charset rules apply here as at
/// creation, and why the change is audited.
/// </remarks>
[Handler("groups.rename")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class RenameGroup(WatchtowerDbContext db, ICurrentUser currentUser, TimeProvider time)
    : IHandler<RenameGroup.Command, Result<RenameGroup.Response>> {

    public sealed record Command(int Id, string Name);
    public sealed record Response(GroupDto Group);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (GroupMapping.ValidateName(command.Name, out var name) is { } invalid)
            return invalid;

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == command.Id, ct);
        if (group is null)
            return AppError.NotFound($"Group {command.Id} not found.");

        var normalized = GroupMapping.Normalize(name);
        // Excluding this group's own row: re-saving a group under a differently-cased spelling of its
        // current name is a rename, not a collision with itself.
        if (await db.Groups.AsNoTracking().AnyAsync(g => g.Id != group.Id && g.NormalizedName == normalized, ct))
            return AppError.Conflict($"A group named '{name}' already exists.");

        var previous = group.Name;
        group.Name = name;
        group.NormalizedName = normalized;
        await db.SaveChangesAsync(ct);

        var memberCount = await db.GroupMembers.CountAsync(m => m.GroupId == group.Id, ct);

        // Past the commit point. The previous name is in the detail because it is the name upstreams were
        // being told until now — an audit row saying only the new one would not explain the change.
        await GroupMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.GroupRenamed, group.Id, group.Name, $"from={previous}");

        return new Response(GroupMapping.ToDto(group, memberCount));
    }
}
