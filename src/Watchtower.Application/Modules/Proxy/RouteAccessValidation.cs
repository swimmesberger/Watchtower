using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>
/// A route access policy as a caller asked for it — the shape <c>proxy.setAccess</c> takes, and the access
/// half of <c>proxy.createRoute</c>.
/// </summary>
/// <param name="AccessRuleIds">
/// <see langword="null"/> means "the caller said nothing about attachments", which <c>setAccess</c> reads as
/// "leave them alone" and a create reads as "none". An explicit empty list is "none" in both.
/// </param>
internal sealed record RouteAccessRequest(
    AccessMode Mode,
    string? BypassPaths,
    IReadOnlyList<int>? GrantedUserIds,
    IReadOnlyList<int>? GrantedGroupIds,
    IReadOnlyList<int>? AccessRuleIds,
    IdentityHeaderMode? IdentityHeaderMode);

/// <summary>
/// A policy that passed <see cref="RouteAccessValidation.ValidateAsync"/>: every field normalized, and only
/// the parts that mean something for its mode kept.
/// </summary>
/// <param name="AccessRuleIds">
/// In precedence order. <see langword="null"/> only for an <see cref="AccessMode.Authenticated"/> request
/// that named no attachments at all — "leave them alone"; every other mode has an empty list, because
/// leaving <c>Authenticated</c> is what makes attachments mean nothing.
/// </param>
internal sealed record ValidatedRouteAccess(
    AccessMode Mode,
    IdentityHeaderMode IdentityHeaderMode,
    string? BypassPaths,
    List<int> GrantedUserIds,
    List<int> GrantedGroupIds,
    List<int>? AccessRuleIds);

/// <summary>
/// The one reading of "is this a valid access policy for a route of that realm" — shared by
/// <c>proxy.setAccess</c> and <c>proxy.createRoute</c>, so what a route may be created with and what it may
/// later be changed to cannot disagree (the two-step create this replaced existed because they did).
/// </summary>
/// <remarks>
/// Validation only, and complete before anything is written: a policy naming one good and one unknown
/// subject is refused whole rather than half-applied. Writing is each caller's, because a create adds rows
/// to a route that does not exist yet while <c>setAccess</c> reconciles against the rows already there.
/// <para>
/// Takes the route's realm rather than the route, because a create has no route yet — its realm is the one
/// its stack's category puts it in, which is also what <see cref="RouteAccessPolicy.RouteRealmIdsAsync"/>
/// resolves for an existing route.
/// </para>
/// </remarks>
internal static class RouteAccessValidation {
    /// <summary>The result: exactly one of the two is set.</summary>
    internal sealed record Outcome(AppError? Error, ValidatedRouteAccess? Access);

    public static async Task<Outcome> ValidateAsync(
        WatchtowerDbContext db,
        RouteAccessRequest request,
        int routeRealmId,
        string routeLabel,
        AccessEnforcementPoint point,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);

        // Reject an undefined enum value before touching anything — an unknown value must not be persisted
        // and read back later as something the switch statements cannot map. Both enums are guarded, fail-
        // closed and symmetric; an omitted identity header mode defaults to the safe JWT-only None.
        if (!Enum.IsDefined(request.Mode))
            return Fail($"Unknown access mode '{request.Mode}'.");
        var identityHeaderMode = request.IdentityHeaderMode ?? IdentityHeaderMode.None;
        if (!Enum.IsDefined(identityHeaderMode))
            return Fail($"Unknown identity header mode '{identityHeaderMode}'.");

        // Bypass paths only mean something for a protected route; a Public route stores none, the same way
        // grants are cleared below for any non-Restricted mode — its access controls are off, so a stale
        // bypass line would only be dead state. Validation therefore applies to the modes that keep them.
        string? bypassPaths = null;
        if (request.Mode != AccessMode.Public) {
            bypassPaths = RouteAccessPolicy.NormalizeBypassPaths(request.BypassPaths, out var offending);
            if (offending is not null)
                return Fail($"Bypass path '{offending}' must start with '/'.");
        }

        // Grants only mean something for a Restricted route; every other mode stores none, so switching away
        // from Restricted clears enforcement (RouteAccessPolicy.IsAuthorizedAsync no longer consults them).
        // Both subject kinds are cleared together — leaving group grants behind while dropping user ones
        // would make "not Restricted" mean something different depending on how access had been granted.
        var userIds = request.Mode == AccessMode.Restricted
            ? (request.GrantedUserIds ?? []).Distinct().ToList()
            : [];
        var groupIds = request.Mode == AccessMode.Restricted
            ? (request.GrantedGroupIds ?? []).Distinct().ToList()
            : [];

        // Attachments belong to Authenticated and only Authenticated (ADR-0039 decision 2). Refused rather
        // than silently dropped for the modes that cannot use them: Restricted means "exactly these subjects"
        // and Public asks nobody, so accepting a rule there would store state that reads like access somebody
        // has while deciding nothing. Distinct rather than rejected on duplicates: naming one rule twice is
        // one attachment, which is what the unique index says too.
        var requestedRuleIds = request.AccessRuleIds?.Distinct().ToList();
        if (requestedRuleIds is { Count: > 0 } && request.Mode != AccessMode.Authenticated) {
            return Fail(
                $"Access rules apply to an {nameof(AccessMode.Authenticated)} route. "
                + (request.Mode == AccessMode.Restricted
                    ? "A Restricted route admits exactly the users and groups granted on it; a rule would widen that."
                    : "A Public route admits everyone, so there is nothing for a rule to decide."));
        }
        var ruleIds = request.Mode == AccessMode.Authenticated ? requestedRuleIds : [];

        // A grant naming a subject from another population would never admit anyone (RouteAccessPolicy
        // applies the same invariant at access time), so it is refused here rather than stored as a row that
        // reads like access somebody has (docs/central-auth/design.md §13).
        if (userIds.Count > 0) {
            var known = await db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.RealmId })
                .ToListAsync(ct);
            var missing = userIds.Except(known.Select(u => u.Id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                return Fail($"No user exists with id {Describe(missing)}.");
            var foreign = known.Where(u => u.RealmId != routeRealmId).Select(u => u.Id).OrderBy(id => id).ToList();
            if (foreign.Count > 0)
                return Fail($"User {Describe(foreign)} belongs to a different realm than {routeLabel}.");
        }

        if (groupIds.Count > 0) {
            var known = await db.Groups.AsNoTracking()
                .Where(g => groupIds.Contains(g.Id))
                .Select(g => new { g.Id, g.RealmId })
                .ToListAsync(ct);
            var missing = groupIds.Except(known.Select(g => g.Id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                return Fail($"No group exists with id {Describe(missing)}.");
            var foreign = known.Where(g => g.RealmId != routeRealmId).Select(g => g.Id).OrderBy(id => id).ToList();
            if (foreign.Count > 0)
                return Fail($"Group {Describe(foreign)} belongs to a different realm than {routeLabel}.");
        }

        // Every attached rule must exist, belong to the route's population, and consist only of clauses the
        // active provider can actually honour (ADR-0039 decision 4). The portability check is the whole point
        // of declaring portability: a reconcile is never where an operator finds out that half of what they
        // asked for was dropped.
        if (ruleIds is { Count: > 0 }) {
            var known = await db.AccessRules.AsNoTracking()
                .Where(r => ruleIds.Contains(r.Id))
                .Select(r => new {
                    r.Id, r.Name, r.RealmId,
                    Kinds = db.AccessRuleClauses.Where(c => c.AccessRuleId == r.Id).Select(c => c.Kind).ToList(),
                })
                .ToListAsync(ct);

            var missing = ruleIds.Except(known.Select(r => r.Id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                return Fail($"No access rule exists with id {Describe(missing)}.");

            var foreign = known.Where(r => r.RealmId != routeRealmId).Select(r => r.Name)
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (foreign.Count > 0)
                return Fail($"Access rule '{string.Join("', '", foreign)}' belongs to a different realm than {routeLabel}.");

            foreach (var rule in known.OrderBy(r => r.Name, StringComparer.Ordinal)) {
                var unsupported = rule.Kinds.Where(k => !AccessClauseSupport.IsSupportedAt(k, point))
                    .Distinct().Order().ToList();
                if (unsupported.Count == 0) continue;
                return Fail(
                    $"Access rule '{rule.Name}' uses {AccessClauseSupport.DescribeAll(unsupported)}, which "
                    + $"{AccessClauseSupport.Describe(point)} cannot enforce. Attaching it would admit fewer "
                    + "people than the rule says. Remove those clauses, or attach a rule this provider can enforce.");
            }
        }

        return new Outcome(null, new ValidatedRouteAccess(
            request.Mode, identityHeaderMode, bypassPaths, userIds, groupIds, ruleIds));
    }

    /// <summary>
    /// The realm a route on <paramref name="stackId"/> would belong to — its category's, or the operator
    /// realm for a standalone stack — or <see langword="null"/> when there is no such stack. The same answer
    /// <see cref="RouteAccessPolicy.RouteRealmIdsAsync"/> gives for a route that already exists on it.
    /// </summary>
    public static async Task<int?> StackRealmIdAsync(WatchtowerDbContext db, int stackId, CancellationToken ct) {
        var row = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId)
            .Select(s => new { RealmId = s.Template == null ? (int?)null : s.Template.RealmId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : row.RealmId ?? Realm.SystemRealmId;
    }

    private static Outcome Fail(string message) => new(AppError.Validation(message), null);

    /// <summary>Renders the ids that could not be resolved for the refusal message.</summary>
    private static string Describe(IReadOnlyList<int> ids) =>
        ids.Count == 1 ? ids[0].ToString(CultureInfo.InvariantCulture) : string.Join(", ", ids);
}

/// <summary>
/// Applies a validated policy to a route that already exists — shared by <c>proxy.setAccess</c> and
/// <c>proxy.updateRoute</c>, so an edit form that saves the route and its access together changes them
/// exactly as the access-only endpoint would, in the caller's one <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// Reconciles rather than replaces: only the grant rows and attachments that fell out of the target set are
/// deleted and only the new ones added, so re-saving an unchanged policy touches no rows and the next
/// reconcile produces an identical request at the edge. Nothing is saved here.
/// </remarks>
internal static class RouteAccessWrite {
    /// <summary>
    /// Stages <paramref name="access"/> on <paramref name="route"/> and returns the attachments the route ends
    /// up with, in precedence order — the requested ones, or the existing ones when the request left them
    /// alone.
    /// </summary>
    public static async Task<List<int>> ApplyAsync(
        WatchtowerDbContext db, Route route, ValidatedRouteAccess access, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(access);

        route.AccessMode = access.Mode;
        route.IdentityHeaderMode = access.IdentityHeaderMode;
        route.BypassPaths = access.BypassPaths;

        var currentGrants = await db.RouteAccessGrants
            .Where(g => g.RouteId == route.Id)
            .ToListAsync(ct);
        var targetUsers = access.GrantedUserIds.ToHashSet();
        var targetGroups = access.GrantedGroupIds.ToHashSet();
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
        foreach (var userId in access.GrantedUserIds.Where(id => !currentUsers.Contains(id)))
            db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = route.Id, UserId = userId });
        foreach (var groupId in access.GrantedGroupIds.Where(id => !currentGroups.Contains(id)))
            db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = route.Id, GroupId = groupId });

        // Attachments by position: Order is the precedence they attach in at the edge. Skipped entirely when
        // the request said nothing about them.
        var currentAttachments = await db.RouteAccessRules
            .Where(a => a.RouteId == route.Id)
            .OrderBy(a => a.Order).ThenBy(a => a.Id)
            .ToListAsync(ct);
        if (access.AccessRuleIds is not { } targetRuleIds)
            return [.. currentAttachments.Select(a => a.AccessRuleId)];

        foreach (var attachment in currentAttachments.Where(a => !targetRuleIds.Contains(a.AccessRuleId)))
            db.RouteAccessRules.Remove(attachment);
        for (var i = 0; i < targetRuleIds.Count; i++) {
            var existing = currentAttachments.FirstOrDefault(a => a.AccessRuleId == targetRuleIds[i]);
            if (existing is null)
                db.RouteAccessRules.Add(new RouteAccessRule { RouteId = route.Id, AccessRuleId = targetRuleIds[i], Order = i });
            else if (existing.Order != i)
                existing.Order = i;
        }
        return targetRuleIds;
    }
}
