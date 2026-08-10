using System.Globalization;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Groups.Handlers;

/// <summary>
/// Replaces a group's membership with the submitted set. Whole-set rather than add/remove: the dialog
/// that drives it shows the whole account roster with checkboxes, so what it knows is the set it wants,
/// and a request that says so cannot leave the group in a state neither side asked for.
/// </summary>
/// <remarks>
/// This is a privilege change, not a directory edit: adding an account here grants it every route the
/// group is named on, and removing one revokes those on the next verified request (the policy evaluator
/// reads membership per request — no cache to invalidate). Hence the same shape as
/// <c>proxy.setAccess</c>: <c>[RequireRole("Admin")]</c>, every id validated before any write so a set
/// naming one good and one unknown account is refused whole, reconciliation rather than
/// delete-and-re-add so re-saving an unchanged set touches no rows, and the audit row written only once
/// the change has committed.
/// </remarks>
[Handler("groups.setMembers")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class SetGroupMembers(WatchtowerDbContext db, ICurrentUser currentUser, TimeProvider time)
    : IHandler<SetGroupMembers.Command, Result<SetGroupMembers.Response>> {

    public sealed record Command(int Id, IReadOnlyList<int> UserIds);
    public sealed record Response(int Id, IReadOnlyList<int> UserIds);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == command.Id, ct);
        if (group is null)
            return AppError.NotFound($"Group {command.Id} not found.");

        var target = (command.UserIds ?? []).Distinct().ToList();

        if (target.Count > 0) {
            var known = await db.Users.AsNoTracking()
                .Where(u => target.Contains(u.Id))
                .Select(u => new { u.Id, u.RealmId })
                .ToListAsync(ct);
            var missing = target.Except(known.Select(u => u.Id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                return AppError.Validation($"No user exists with id {Describe(missing)}.");

            // A group holds accounts of its own population and no other (design.md §13). Refused at write
            // time as well as ignored at access time, because a membership that can never take effect is
            // an administrator's mistake worth naming rather than a row to leave lying around: the route
            // grants the group unlocks are all in the group's realm, so a foreign member would show up in
            // the roster as having access it does not have.
            var foreign = known
                .Where(u => u.RealmId != group.RealmId)
                .Select(u => u.Id)
                .OrderBy(id => id)
                .ToList();
            if (foreign.Count > 0) {
                return AppError.Validation(
                    $"User {Describe(foreign)} belongs to a different realm than group '{group.Name}'.");
            }
        }

        // Reconcile rather than replace: delete only the rows that fell out of the set, add only the ones
        // that entered it. Re-saving an unchanged set touches no membership rows.
        var current = await db.GroupMembers.Where(m => m.GroupId == group.Id).ToListAsync(ct);
        var targetSet = target.ToHashSet();
        var currentSet = current.Select(m => m.UserId).ToHashSet();

        var removed = 0;
        foreach (var member in current.Where(m => !targetSet.Contains(m.UserId))) {
            db.GroupMembers.Remove(member);
            removed++;
        }
        var added = 0;
        foreach (var userId in target.Where(id => !currentSet.Contains(id))) {
            db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId });
            added++;
        }

        await db.SaveChangesAsync(ct);

        // Past the commit point. The deltas are recorded rather than the resulting set: what an operator
        // reading the trail after an unexpected denial needs is who stopped being a member and when.
        await GroupMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.GroupMembersChanged, group.Id, group.Name,
            $"members={target.Count}; added={added}; removed={removed}");

        return new Response(group.Id, [.. target.Order()]);
    }

    /// <summary>Renders the ids that could not be resolved for the refusal message.</summary>
    private static string Describe(IReadOnlyList<int> missing) =>
        missing.Count == 1
            ? missing[0].ToString(CultureInfo.InvariantCulture)
            : string.Join(", ", missing);
}
