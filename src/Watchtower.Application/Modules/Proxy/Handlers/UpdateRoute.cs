using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Updates a route's domain/target/TLS settings, then reconciles the proxy.</summary>
/// <remarks>
/// <see cref="Route.Target"/> is <b>not</b> editable (ADR-0023), and neither is
/// <see cref="Route.Binding"/> (ADR-0033). Each kind is a different row with different required columns,
/// and flipping one in place would silently move a live address rather than edit it — delete and recreate
/// says what is happening.
/// </remarks>
[Handler("proxy.updateRoute")]
public sealed class UpdateRoute(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    IOptionsMonitor<WatchtowerOptions> options,
    YarpListenerState listener,
    HostPortOccupancy hostPorts,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<UpdateRoute.Command, Result<UpdateRoute.Response>> {
    /// <param name="Domain">
    /// The hostname to serve. Required for a domain route; a port route has none, and sending one is
    /// refused rather than ignored.
    /// </param>
    /// <param name="MakeLoginRoute">
    /// Watchtower routes only: <see langword="true"/> designates this route as its realm's login host,
    /// <see langword="false"/> clears the designation if this route currently holds it, and omitting it
    /// leaves the realm alone.
    /// </param>
    /// <param name="Binding">
    /// Sent back for confirmation only. Omitting it leaves the binding alone (which is the only thing an
    /// edit can do with it); naming a different one than the row's is refused.
    /// </param>
    /// <param name="ListenPort">
    /// Port routes only: move the route to another host port. Omitting it keeps the current one.
    /// </param>
    /// <param name="AccessMode">
    /// Service domain routes only, administrators only: the route's whole access policy, replaced together
    /// with its other fields in one write — what lets one edit form change a route and who reaches it without
    /// a second call that could half-apply. Omitted means "leave access alone", which is what every client
    /// predating this sends. When named, the rest of the policy means exactly what it means on
    /// <c>proxy.setAccess</c>, and is validated by the same code.
    /// </param>
    public sealed record Command(
        int Id,
        string? Domain,
        string ServiceName,
        int ContainerPort,
        bool TlsEnabled,
        bool IsPrimary,
        string? Kind = null,
        bool? MakeLoginRoute = null,
        string? Binding = null,
        int? ListenPort = null,
        AccessMode? AccessMode = null,
        string? BypassPaths = null,
        IReadOnlyList<int>? GrantedUserIds = null,
        IReadOnlyList<int>? GrantedGroupIds = null,
        IReadOnlyList<int>? AccessRuleIds = null,
        IdentityHeaderMode? IdentityHeaderMode = null);

    /// <summary>Whether the request says anything about access at all.</summary>
    private static bool NamesAccess(Command command) =>
        command.AccessMode is not null || command.BypassPaths is not null
        || command.GrantedUserIds is { Count: > 0 } || command.GrantedGroupIds is { Count: > 0 }
        || command.AccessRuleIds is not null || command.IdentityHeaderMode is not null;

    public sealed record Response(RouteDto Route);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (route is null)
            return AppError.NotFound($"Route {command.Id} not found");

        // Access is all or nothing on an edit: a policy without its mode would leave the reader guessing
        // which mode the grants or rules were meant for, so the mode is what makes the rest meaningful.
        if (NamesAccess(command)) {
            if (!currentUser.IsInRole(WatchtowerClaims.AdminRole)) {
                return AppError.Forbidden(
                    "Only an administrator can change a route's access policy. Leave the access fields unset "
                    + "to edit the rest of the route.");
            }
            if (command.AccessMode is null) {
                return AppError.Validation(
                    "Name the access mode together with the rest of the access policy, or leave all of it unset "
                    + "to keep the route's current access.");
            }
            // The same refusals proxy.setAccess makes, for the same reasons: their access is structural.
            if (route.Target == RouteTarget.Watchtower) {
                return AppError.Validation(
                    "Watchtower routes use Watchtower's own login; route access control does not apply.");
            }
            if (route.Binding == RouteBinding.Port) {
                return AppError.Validation(
                    "A port route is always public. It has no hostname for a login redirect to return to, so "
                    + "route access control does not apply.");
            }
        }

        if (command.Binding is not null) {
            if (!RouteMapping.TryParseBinding(command.Binding, out var requested))
                return AppError.Validation($"Unknown route binding '{command.Binding}'. Use 'domain' or 'port'.");
            if (requested != route.Binding) {
                return AppError.Validation(
                    "A route's binding is fixed: a domain route and a port route are addressed by "
                    + "different things and fill different columns. Delete this route and create the "
                    + "other kind instead.");
            }
        }

        if (route.Binding == RouteBinding.Port)
            return await UpdatePortRouteAsync(route, command, ct);

        if (command.ListenPort is not null) {
            return AppError.Validation(
                "A domain route is addressed by its hostname; listenPort applies to port routes only.");
        }

        // The same rules the certificate machinery applies, at the point the name is typed: a domain a
        // CA would never issue for is worth refusing here rather than discovering as a route that never
        // leaves "pending" (Services/Acme/DesiredHosts.cs).
        if (!DesiredHosts.TryNormalize(command.Domain, out var domain, out var reason))
            return AppError.Validation(reason);
        if (await db.Routes.AnyAsync(r => r.Domain == domain && r.Id != command.Id, ct))
            return AppError.Validation($"Domain '{domain}' is already routed.");

        return route.Target == RouteTarget.Watchtower
            ? await UpdateWatchtowerRouteAsync(route, command, domain, ct)
            : await UpdateServiceRouteAsync(route, command, domain, ct);
    }

    private async ValueTask<Result<Response>> UpdateServiceRouteAsync(
        Route route, Command command, string domain, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(command.ServiceName))
            return AppError.Validation("Service name is required.");
        if (command.ContainerPort is < 1 or > 65535)
            return AppError.Validation("Container port must be between 1 and 65535.");
        if (command.MakeLoginRoute is not null) {
            return AppError.Validation(
                "Only a Watchtower route can be a realm's login host. Create one for the hostname the " +
                "Watchtower UI is served on instead.");
        }

        // Validated in full before anything is staged, by the code proxy.setAccess and proxy.createRoute run,
        // so an edit that is refused for its access leaves the route's other fields untouched as well.
        ValidatedRouteAccess? access = null;
        if (command.AccessMode is { } mode) {
            var realmId = await RouteAccessPolicy.RouteRealmIdAsync(db, route.Id, ct);
            if (realmId is null)
                return AppError.NotFound($"Route {command.Id} not found");
            var validation = await RouteAccessValidation.ValidateAsync(
                db,
                new RouteAccessRequest(
                    mode, command.BypassPaths, command.GrantedUserIds, command.GrantedGroupIds,
                    command.AccessRuleIds, command.IdentityHeaderMode),
                realmId.Value,
                domain,
                AccessClauseSupport.PointFor(options.CurrentValue.Proxy.ResolveProvider()),
                ct);
            if (validation.Error is { } invalid) return invalid;
            access = validation.Access;
        }

        route.Domain = domain;
        route.ServiceName = command.ServiceName.Trim();
        route.ContainerPort = command.ContainerPort;
        route.TlsEnabled = command.TlsEnabled;
        route.IsPrimary = command.IsPrimary;
        if (command.Kind is not null) route.Kind = RouteMapping.ParseKind(command.Kind);
        // Staged by the same writer proxy.setAccess uses, and saved in the one SaveChanges below with the
        // route's other fields — the edit form changes a route and who reaches it as a single write.
        if (access is not null) await RouteAccessWrite.ApplyAsync(db, route, access, ct);
        await db.SaveChangesAsync(ct);

        // StackId is non-null on a service route by the check constraint; the compiler cannot see that.
        await proxy.ConnectStackAsync(route.StackId!.Value, ct);
        await proxy.ApplyAsync(ct);

        // Past the commit point, and the same trace proxy.setAccess leaves for the same decision.
        if (access is not null)
            await RouteAccessAudit.RecordAsync(db, currentUser, time, route, access.Mode, "on edit");

        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }

    private async ValueTask<Result<Response>> UpdateWatchtowerRouteAsync(
        Route route, Command command, string domain, CancellationToken ct) {
        // Refused rather than ignored, for the same reason CreateRoute refuses them: a caller that filled
        // in a service and a port is describing a different route than the one it is editing.
        if (!string.IsNullOrWhiteSpace(command.ServiceName) || command.ContainerPort != 0) {
            return AppError.Validation(
                "A Watchtower route forwards nowhere; leave the service name and container port unset.");
        }

        var realm = await db.Realms.FirstAsync(r => r.Id == route.RealmId, ct);
        if (RouteMapping.CheckAuthHostCollision(domain, realm, options.CurrentValue.Auth.Host) is { } clash)
            return clash;

        route.Domain = domain;
        route.TlsEnabled = command.TlsEnabled;
        route.IsPrimary = command.IsPrimary;
        if (command.Kind is not null) route.Kind = RouteMapping.ParseKind(command.Kind);

        if (command.MakeLoginRoute is true) realm.LoginRouteId = route.Id;
        else if (command.MakeLoginRoute is false && realm.LoginRouteId == route.Id) realm.LoginRouteId = null;

        await db.SaveChangesAsync(ct);
        await proxy.ApplyAsync(ct);

        var saved = await db.Routes.AsNoTracking()
            .Include(r => r.Realm)
            .FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved, isLoginRoute: realm.LoginRouteId == route.Id));
    }

    /// <summary>
    /// Edits a <see cref="RouteBinding.Port"/> route: where it forwards, and which host port it answers
    /// on (ADR-0033). Moving the port rewrites the derived listener setting through
    /// <c>ApplyAsync</c>, so Kestrel unbinds the old listener and binds the new one without a restart.
    /// </summary>
    /// <remarks>
    /// The hostname fields are refused rather than ignored, the same way the Watchtower branch refuses a
    /// service and a port: a caller that filled them in is describing a different route than the one it
    /// is editing. What is left — TLS and primacy — is fixed by what a port route is, exactly as at
    /// creation, so an edit cannot turn one into something the check constraint would not have stored.
    /// </remarks>
    private async ValueTask<Result<Response>> UpdatePortRouteAsync(
        Route route, Command command, CancellationToken ct) {
        if (!string.IsNullOrWhiteSpace(command.Domain)) {
            return AppError.Validation(
                "A port route has no hostname — it is reached by port. Leave the domain empty.");
        }
        if (command.MakeLoginRoute is not null) {
            return AppError.Validation(
                "Only a Watchtower route can be a realm's login host, and a port route has no hostname to "
                + "be one on.");
        }
        if (command.Kind is not null) {
            return AppError.Validation(
                "A port route has no hostname, so it is neither a managed subdomain nor a custom domain; "
                + "leave the kind unset.");
        }
        if (string.IsNullOrWhiteSpace(command.ServiceName))
            return AppError.Validation("Service name is required.");
        if (command.ContainerPort is < 1 or > 65535)
            return AppError.Validation("Container port must be between 1 and 65535.");

        // Omitted means "leave the port alone", which is what an edit of the upstream alone sends.
        var listenPort = command.ListenPort ?? route.ListenPort
            ?? throw new InvalidOperationException(
                $"Route {route.Id} is a port route with no listen port; ck_routes_binding forbids that row.");
        if (listenPort != route.ListenPort) {
            var yarp = options.CurrentValue.Proxy.Yarp;
            if (PortRouteRules.ValidateListenPort(listenPort, listener.ManagementPort, yarp) is { } portError)
                return AppError.Validation(portError);
            if (await PortRouteRules.TakenByAsync(db, listenPort, exceptRouteId: route.Id, ct) is { } clash)
                return AppError.Validation(clash);
            // The move has to land on a port nothing else on this host publishes, for the reason a
            // creation does: the listener lives on Watchtower's own container.
            if (await hostPorts.PublishedByAnotherContainerAsync(
                    listenPort, selfContainerId: null, ct) is { } held)
                return AppError.Validation(held);
        }

        route.ListenPort = listenPort;
        route.ServiceName = command.ServiceName.Trim();
        route.ContainerPort = command.ContainerPort;
        try {
            await db.SaveChangesAsync(ct);
        } catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) {
            return AppError.Conflict(
                $"Port {listenPort} was taken by another route while this one was being edited.");
        }

        // StackId is non-null on a service route by the check constraint, and a port route is always one.
        await proxy.ConnectStackAsync(route.StackId!.Value, ct);
        await proxy.ApplyAsync(ct);

        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }
}
