using System.Globalization;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Writes a route's access policy (docs/central-auth/design.md §7): its <see cref="AccessMode"/>, bypass
/// paths and — for <see cref="AccessMode.Restricted"/> — the sets of users and groups allowed through.
/// Reconciles the <see cref="RouteAccessGrant"/> rows to the target sets and reloads the proxy, because
/// turning access control on or off for a route changes whether Caddy emits a <c>forward_auth</c> block
/// for it.
/// </summary>
/// <remarks>
/// Same <c>[RequireRole("Admin")]</c> as the rest of the access surface. The write is fail-fast: an
/// unparseable bypass line or an unknown user or group id is rejected as <c>Validation</c> before anything
/// is persisted, so a partially-applied policy is never committed. Grants are reconciled (removed rows deleted,
/// new rows added) rather than deleted-and-re-added, so re-saving an unchanged <c>Restricted</c> route
/// churns no rows. The audit row and the Caddy reload both run only after the policy has committed — the
/// same post-commit discipline the Users module uses (see <see cref="UserMapping.RecordAsync"/>), so a
/// caller that hangs up mid-request cannot keep its own administrative change out of the trail.
/// </remarks>
[Handler("proxy.setAccess")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class SetAccess(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<SetAccess.Command, Result<SetAccess.Response>> {

    public sealed record Command(
        int RouteId,
        AccessMode Mode,
        string? BypassPaths,
        IReadOnlyList<int> GrantedUserIds,
        // Optional and last (a default value is what marks a param non-required in the generated schema): an
        // older client that omits it keeps identity forwarding at the safe JWT-only default rather than being
        // rejected, so the field is a purely additive, non-breaking addition to the wire contract.
        IdentityHeaderMode? IdentityHeaderMode = null,
        // Added the same way and for the same reason, which is why it goes after IdentityHeaderMode rather
        // than next to GrantedUserIds: a client that predates group grants omits it and gets today's
        // behaviour — the user grants it did send, and no group grants — instead of a rejected write.
        IReadOnlyList<int>? GrantedGroupIds = null);

    public sealed record Response(
        AccessMode Mode,
        IdentityHeaderMode IdentityHeaderMode,
        string? BypassPaths,
        IReadOnlyList<int> GrantedUserIds,
        IReadOnlyList<int> GrantedGroupIds);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == command.RouteId, ct);
        if (route is null)
            return AppError.NotFound($"Route {command.RouteId} not found");

        // A Watchtower route is served by Watchtower, which authenticates its own visitors (ADR-0021).
        // There is no forward-auth hop to configure, the check constraint refuses anything but Public,
        // and putting a login page behind the gate that redirects to it is a closed loop. Refused rather
        // than silently accepted as a no-op: an administrator who thought they had gated a hostname must
        // find out that they have not.
        if (route.Target == RouteTarget.Watchtower) {
            return AppError.Validation(
                "Watchtower routes use Watchtower's own login; route access control does not apply.");
        }

        // Reject an undefined enum value before touching anything — an unknown value must not be persisted
        // and read back later as something the switch statements cannot map. Both enums are guarded, fail-
        // closed and symmetric; an omitted identity header mode defaults to the safe JWT-only None.
        if (!Enum.IsDefined(command.Mode))
            return AppError.Validation($"Unknown access mode '{command.Mode}'.");
        var identityHeaderMode = command.IdentityHeaderMode ?? IdentityHeaderMode.None;
        if (!Enum.IsDefined(identityHeaderMode))
            return AppError.Validation($"Unknown identity header mode '{identityHeaderMode}'.");

        // Bypass paths only mean something for a protected route; a Public route stores none, the same way
        // grants are cleared below for any non-Restricted mode — its access controls are off, so a stale
        // bypass line would only be dead state. Validation therefore applies to the modes that keep them.
        string? bypassPaths = null;
        if (command.Mode != AccessMode.Public) {
            bypassPaths = NormalizeBypassPaths(command.BypassPaths, out var offending);
            if (offending is not null)
                return AppError.Validation($"Bypass path '{offending}' must start with '/'.");
        }

        // Grants only mean something for a Restricted route; every other mode stores none, so switching away
        // from Restricted clears enforcement (RouteAccessPolicy.IsAuthorizedAsync no longer consults them).
        // Both subject kinds are cleared together — leaving group grants behind while dropping user ones
        // would make "not Restricted" mean something different depending on how access had been granted.
        var targetUserGrants = command.Mode == AccessMode.Restricted
            ? (command.GrantedUserIds ?? []).Distinct().ToList()
            : [];
        var targetGroupGrants = command.Mode == AccessMode.Restricted
            ? (command.GrantedGroupIds ?? []).Distinct().ToList()
            : [];

        // The realm the route belongs to — its stack's category, or the operator realm for a standalone
        // stack. A grant naming a subject from any other population would never admit anyone
        // (RouteAccessPolicy applies the same invariant at access time), so it is refused here rather than
        // stored as a row that reads like access somebody has (docs/central-auth/design.md §13).
        var routeRealmId = await RouteAccessPolicy.RouteRealmIdAsync(db, route.Id, ct);
        if (routeRealmId is null)
            return AppError.NotFound($"Route {command.RouteId} not found");

        // Both existence checks run before any write, so a command naming one good and one unknown subject
        // is refused whole rather than half-applied.
        if (targetUserGrants.Count > 0) {
            var known = await db.Users.AsNoTracking()
                .Where(u => targetUserGrants.Contains(u.Id))
                .Select(u => new { u.Id, u.RealmId })
                .ToListAsync(ct);
            var missing = targetUserGrants.Except(known.Select(u => u.Id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                return AppError.Validation($"No user exists with id {Describe(missing)}.");

            var foreign = known.Where(u => u.RealmId != routeRealmId).Select(u => u.Id).OrderBy(id => id).ToList();
            if (foreign.Count > 0) {
                return AppError.Validation(
                    $"User {Describe(foreign)} belongs to a different realm than {route.Domain}.");
            }
        }

        if (targetGroupGrants.Count > 0) {
            var known = await db.Groups.AsNoTracking()
                .Where(g => targetGroupGrants.Contains(g.Id))
                .Select(g => new { g.Id, g.RealmId })
                .ToListAsync(ct);
            var missing = targetGroupGrants.Except(known.Select(g => g.Id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                return AppError.Validation($"No group exists with id {Describe(missing)}.");

            var foreign = known.Where(g => g.RealmId != routeRealmId).Select(g => g.Id).OrderBy(id => id).ToList();
            if (foreign.Count > 0) {
                return AppError.Validation(
                    $"Group {Describe(foreign)} belongs to a different realm than {route.Domain}.");
            }
        }

        route.AccessMode = command.Mode;
        route.IdentityHeaderMode = identityHeaderMode;
        route.BypassPaths = bypassPaths;

        // Reconcile rather than replace: delete only the rows that fell out of the set, add only the ones
        // that entered it. Re-saving an unchanged set touches no grant rows.
        var currentGrants = await db.RouteAccessGrants
            .Where(g => g.RouteId == route.Id)
            .ToListAsync(ct);
        var targetUsers = targetUserGrants.ToHashSet();
        var targetGroups = targetGroupGrants.ToHashSet();
        var currentUsers = currentGrants.Where(g => g.UserId is not null).Select(g => g.UserId!.Value).ToHashSet();
        var currentGroups = currentGrants.Where(g => g.GroupId is not null).Select(g => g.GroupId!.Value).ToHashSet();

        // Each row is judged against the target set of its own subject kind. A row that is somehow neither
        // (which the table's CHECK constraint forbids) matches nothing and is removed — the fail-closed
        // reading, since a grant naming no subject is one nobody can account for.
        foreach (var grant in currentGrants) {
            var keep = grant.UserId is { } userId
                ? targetUsers.Contains(userId)
                : grant.GroupId is { } groupId && targetGroups.Contains(groupId);
            if (!keep) db.RouteAccessGrants.Remove(grant);
        }
        foreach (var userId in targetUserGrants.Where(id => !currentUsers.Contains(id)))
            db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = route.Id, UserId = userId });
        foreach (var groupId in targetGroupGrants.Where(id => !currentGroups.Contains(id)))
            db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = route.Id, GroupId = groupId });

        await db.SaveChangesAsync(ct);

        // Protected-ness may have flipped, so the generated Caddyfile changes — reload it. Best-effort like
        // the route CRUD handlers: a proxy hiccup must not fail a policy change that already committed.
        await proxy.ApplyAsync(ct);

        // Past the commit point: record the change uncancellably (CancellationToken.None inside).
        await RecordAsync(route, command.Mode);

        return new Response(
            route.AccessMode,
            route.IdentityHeaderMode,
            route.BypassPaths,
            [.. targetUserGrants.Order()],
            [.. targetGroupGrants.Order()]);
    }

    /// <summary>Renders the ids that could not be resolved for the refusal message.</summary>
    private static string Describe(IReadOnlyList<int> missing) =>
        missing.Count == 1
            ? missing[0].ToString(CultureInfo.InvariantCulture)
            : string.Join(", ", missing);

    /// <summary>
    /// Trims each bypass line, drops the blanks, and rejects the first non-empty line that is not a rooted
    /// path (a prefix that cannot occur in a request path would only ever be dead configuration — see
    /// <see cref="RouteAccessPolicy.ParseBypassPaths"/>, which silently drops such lines on the read side).
    /// Returns the newline-joined survivors, or <see langword="null"/> when nothing is left.
    /// </summary>
    private static string? NormalizeBypassPaths(string? raw, out string? offending) {
        offending = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var lines = new List<string>();
        foreach (var line in raw.Split('\n')) {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed[0] != '/') {
                offending = trimmed;
                return null;
            }
            lines.Add(trimmed);
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>
    /// Appends the audit row for a policy change: the route is the target, the acting administrator the
    /// actor (resolved to a name; the implicit local administrator records as <c>local</c>, design.md §2.6).
    /// </summary>
    private async Task RecordAsync(Route route, AccessMode mode) {
        var actorId = string.IsNullOrEmpty(currentUser.UserId) ? "unknown" : currentUser.UserId;
        db.AuditEvents.Add(new AuditEvent {
            Category = AuthEventKinds.CategoryOf(AuthEventKinds.RouteAccessChanged),
            Action = AuthEventKinds.RouteAccessChanged,
            Target = route.Domain,
            Detail = $"actor={actorId}; route={route.Domain}#{route.Id}; mode={mode}",
            Actor = await AuditLog.ResolveActorAsync(db, currentUser.UserId),
            Success = true,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
