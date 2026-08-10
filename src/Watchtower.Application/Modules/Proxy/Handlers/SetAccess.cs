using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Writes a route's access policy (docs/central-auth/design.md §7): its <see cref="AccessMode"/>, bypass
/// paths and — for <see cref="AccessMode.Restricted"/> — the set of users allowed through. Reconciles the
/// <see cref="RouteAccessGrant"/> rows to the target set and reloads the proxy, because turning access
/// control on or off for a route changes whether Caddy emits a <c>forward_auth</c> block for it.
/// </summary>
/// <remarks>
/// Same <c>[RequireRole("Admin")]</c> as the rest of the access surface. The write is fail-fast: an
/// unparseable bypass line or an unknown user id is rejected as <c>Validation</c> before anything is
/// persisted, so a partially-applied policy is never committed. Grants are reconciled (removed rows deleted,
/// new rows added) rather than deleted-and-re-added, so re-saving an unchanged <c>Restricted</c> route
/// churns no rows. The audit row and the Caddy reload both run only after the policy has committed — the
/// same post-commit discipline the Users module uses (see <see cref="UserMapping.RecordAsync"/>), so a
/// caller that hangs up mid-request cannot keep its own administrative change out of the trail.
/// </remarks>
[Handler("proxy.setAccess")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class SetAccess(
    WatchtowerDbContext db,
    CaddyManager caddy,
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
        IdentityHeaderMode? IdentityHeaderMode = null);

    public sealed record Response(
        AccessMode Mode,
        IdentityHeaderMode IdentityHeaderMode,
        string? BypassPaths,
        IReadOnlyList<int> GrantedUserIds);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == command.RouteId, ct);
        if (route is null)
            return AppError.NotFound($"Route {command.RouteId} not found");

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
        var targetGrants = command.Mode == AccessMode.Restricted
            ? (command.GrantedUserIds ?? []).Distinct().ToList()
            : [];

        if (targetGrants.Count > 0) {
            var known = await db.Users.AsNoTracking()
                .Where(u => targetGrants.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);
            var missing = targetGrants.Except(known).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                return AppError.Validation(
                    $"No user exists with id {(missing.Count == 1 ? missing[0].ToString() : string.Join(", ", missing))}.");
        }

        route.AccessMode = command.Mode;
        route.IdentityHeaderMode = identityHeaderMode;
        route.BypassPaths = bypassPaths;

        // Reconcile rather than replace: delete only the rows that fell out of the set, add only the ones
        // that entered it. Re-saving an unchanged set touches no grant rows.
        var currentGrants = await db.RouteAccessGrants
            .Where(g => g.RouteId == route.Id)
            .ToListAsync(ct);
        var target = targetGrants.ToHashSet();
        var current = currentGrants.Select(g => g.UserId).ToHashSet();

        foreach (var grant in currentGrants.Where(g => !target.Contains(g.UserId)))
            db.RouteAccessGrants.Remove(grant);
        foreach (var userId in targetGrants.Where(id => !current.Contains(id)))
            db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = route.Id, UserId = userId });

        await db.SaveChangesAsync(ct);

        // Protected-ness may have flipped, so the generated Caddyfile changes — reload it. Best-effort like
        // the route CRUD handlers: a proxy hiccup must not fail a policy change that already committed.
        await caddy.ApplyAsync(ct);

        // Past the commit point: record the change uncancellably (CancellationToken.None inside).
        await RecordAsync(route, command.Mode);

        return new Response(
            route.AccessMode, route.IdentityHeaderMode, route.BypassPaths, [.. targetGrants.OrderBy(id => id)]);
    }

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
    /// Appends the audit row for a policy change. The acting administrator lives in the detail rather than in
    /// <see cref="AuthEvent.UserId"/> — the actor may be the implicit local administrator, which is no real
    /// row (design.md §2.6); the route is the subject, so it is the foreign key that is set.
    /// </summary>
    private async Task RecordAsync(Route route, AccessMode mode) {
        var actorId = string.IsNullOrEmpty(currentUser.UserId) ? "unknown" : currentUser.UserId;
        db.AuthEvents.Add(new AuthEvent {
            Kind = AuthEventKinds.RouteAccessChanged,
            UserId = null,
            RouteId = route.Id,
            Detail = $"actor={actorId}; route={route.Domain}#{route.Id}; mode={mode}",
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
