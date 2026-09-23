using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
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
    IOptionsMonitor<WatchtowerOptions> options,
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
        IReadOnlyList<int>? GrantedGroupIds = null,
        // The access rules this route attaches, in precedence order (ADR-0039). Added last for the same
        // reason again: a client that predates rules omits it and keeps the instance-wide fallback. An
        // explicit empty list detaches everything, which is how a route goes back to that fallback.
        IReadOnlyList<int>? AccessRuleIds = null);

    public sealed record Response(
        AccessMode Mode,
        IdentityHeaderMode IdentityHeaderMode,
        string? BypassPaths,
        IReadOnlyList<int> GrantedUserIds,
        IReadOnlyList<int> GrantedGroupIds,
        IReadOnlyList<int>? AccessRuleIds = null);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == command.RouteId, ct);
        if (route is null)
            return AppError.NotFound($"Route {command.RouteId} not found");

        // A Watchtower route is served by Watchtower, which authenticates its own visitors (ADR-0023).
        // There is no forward-auth hop to configure, the check constraint refuses anything but Public,
        // and putting a login page behind the gate that redirects to it is a closed loop. Refused rather
        // than silently accepted as a no-op: an administrator who thought they had gated a hostname must
        // find out that they have not.
        if (route.Target == RouteTarget.Watchtower) {
            return AppError.Validation(
                "Watchtower routes use Watchtower's own login; route access control does not apply.");
        }

        // The same refusal for the same reason, one axis over (ADR-0033): a port route has no hostname,
        // so an anonymous visitor sent to a login page would have nowhere to be sent back to — and the
        // check constraint refuses anything but Public anyway.
        if (route.Binding == RouteBinding.Port) {
            return AppError.Validation(
                "A port route is always public. It has no hostname for a login redirect to return to, so "
                + "route access control does not apply.");
        }

        // The realm the route belongs to — its stack's category, or the operator realm for a standalone
        // stack — is the population every grant and rule must come from.
        var routeRealmId = await RouteAccessPolicy.RouteRealmIdAsync(db, route.Id, ct);
        if (routeRealmId is null)
            return AppError.NotFound($"Route {command.RouteId} not found");

        // The same validation proxy.createRoute runs, so what a route may be created with and what it may
        // later be changed to cannot disagree.
        var validation = await RouteAccessValidation.ValidateAsync(
            db,
            new RouteAccessRequest(
                command.Mode, command.BypassPaths, command.GrantedUserIds, command.GrantedGroupIds,
                command.AccessRuleIds, command.IdentityHeaderMode),
            routeRealmId.Value,
            route.DisplayAddress,
            AccessClauseSupport.PointFor(options.CurrentValue.Proxy.ResolveProvider()),
            ct);
        if (validation.Error is { } invalid) return invalid;
        var access = validation.Access!;

        // Staged by the same writer proxy.updateRoute uses, so the two endpoints change a policy identically.
        var attachedRuleIds = await RouteAccessWrite.ApplyAsync(db, route, access, ct);

        await db.SaveChangesAsync(ct);

        // Protected-ness may have flipped, so the generated Caddyfile changes — reload it. Best-effort like
        // the route CRUD handlers: a proxy hiccup must not fail a policy change that already committed.
        await proxy.ApplyAsync(ct);

        // Past the commit point: record the change uncancellably (CancellationToken.None inside).
        await RouteAccessAudit.RecordAsync(db, currentUser, time, route, command.Mode);

        return new Response(
            route.AccessMode,
            route.IdentityHeaderMode,
            route.BypassPaths,
            [.. access.GrantedUserIds.Order()],
            [.. access.GrantedGroupIds.Order()],
            // Not sorted: the caller's order is the precedence, so echoing it back is what lets a form
            // round-trip what it saved. A caller that said nothing gets what the route already had, which is
            // the honest answer rather than an empty list it might then save back.
            attachedRuleIds);
    }
}
