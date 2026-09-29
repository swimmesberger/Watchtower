using Elarion.Abstractions.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>
/// The audit row for a route access decision — written by <c>proxy.setAccess</c> and by a
/// <c>proxy.createRoute</c> that names a policy, so granting access at creation leaves the same trace as
/// granting it afterwards.
/// </summary>
/// <remarks>
/// Past the commit point, like every audit write in this plane (see <c>UserMapping.RecordAsync</c>): the
/// save is uncancellable, so a caller that hangs up mid-request cannot keep its own administrative change
/// out of the trail. The actor is resolved to a name; the implicit local administrator records as
/// <c>local</c> (design.md §2.6).
/// </remarks>
internal static class RouteAccessAudit {
    public static async Task RecordAsync(
        WatchtowerDbContext db,
        ICurrentUser currentUser,
        TimeProvider time,
        Route route,
        AccessMode mode,
        string? note = null) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(route);

        var actorId = string.IsNullOrEmpty(currentUser.UserId) ? "unknown" : currentUser.UserId;
        db.AuditEvents.Add(new AuditEvent {
            Category = AuthEventKinds.CategoryOf(AuthEventKinds.RouteAccessChanged),
            Action = AuthEventKinds.RouteAccessChanged,
            Target = route.DisplayAddress,
            Detail = $"actor={actorId}; route={route.DisplayAddress}#{route.Id}; mode={mode}"
                + (note is null ? "" : $"; {note}"),
            Actor = await AuditLog.ResolveActorAsync(db, currentUser.UserId),
            Success = true,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
