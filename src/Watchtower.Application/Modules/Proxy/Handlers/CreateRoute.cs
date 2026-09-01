using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Creates a route. A <see cref="RouteTarget.Service"/> route (the default) maps a domain to a service
/// inside a stack: it is persisted, the target service container is joined to the edge network, and the
/// proxy is reloaded. A <see cref="RouteTarget.Watchtower"/> route maps a domain to <em>this instance</em>
/// (ADR-0023) — no stack, no upstream, and optionally the realm's login host. A
/// <see cref="RouteBinding.Port"/> route (ADR-0033) has no hostname at all: it is reached on a TLS
/// listener of its own, certified by Watchtower's internal CA. The proxy work is a no-op when the reverse
/// proxy is disabled.
/// </summary>
[Handler("proxy.createRoute")]
public sealed class CreateRoute(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    IOptionsMonitor<WatchtowerOptions> options,
    YarpListenerState listener,
    DockerEngineClient docker,
    ILogger<CreateRoute> logger)
    : IHandler<CreateRoute.Command, Result<CreateRoute.Response>> {
    /// <param name="Domain">
    /// The hostname to serve. Required for a <c>domain</c> route and refused on a <c>port</c> one, which
    /// has no hostname to be reached by.
    /// </param>
    /// <param name="Target">
    /// <c>service</c> (the default, and what a client predating ADR-0023 means by saying nothing) or
    /// <c>watchtower</c>.
    /// </param>
    /// <param name="RealmId">
    /// Watchtower routes only: the realm whose surface this hostname serves. Defaults to the system realm.
    /// </param>
    /// <param name="MakeLoginRoute">
    /// Watchtower routes only: designate this route as the realm's login host. Left unset it means "yes,
    /// if the realm has none yet" — creating the first Watchtower route of a realm and then finding its
    /// apps still cannot redirect anywhere would be a trap.
    /// </param>
    /// <param name="Binding">
    /// <c>domain</c> (the default, and what a client predating ADR-0033 means by saying nothing) or
    /// <c>port</c>.
    /// </param>
    /// <param name="ListenPort">
    /// Port routes only: the host port this route's own TLS listener answers on. The port has to be
    /// published on Watchtower's own container as well — nothing here can do that.
    /// </param>
    public sealed record Command(
        int StackId,
        string? Domain,
        string ServiceName,
        int ContainerPort,
        bool TlsEnabled,
        bool IsPrimary,
        string? Kind,
        string? Target = null,
        int? RealmId = null,
        bool? MakeLoginRoute = null,
        string? Binding = null,
        int? ListenPort = null);

    public sealed record Response(RouteDto Route);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);

        if (!RouteMapping.TryParseTarget(command.Target, out var target))
            return AppError.Validation($"Unknown route target '{command.Target}'. Use 'service' or 'watchtower'.");
        if (!RouteMapping.TryParseBinding(command.Binding, out var binding))
            return AppError.Validation($"Unknown route binding '{command.Binding}'. Use 'domain' or 'port'.");

        // Split before the hostname is normalized: a port route has none, so running the domain rules
        // over its empty field would refuse it with a message about a value it is right not to have sent.
        if (binding == RouteBinding.Port)
            return await CreatePortRouteAsync(command, target, ct);

        if (command.ListenPort is not null) {
            return AppError.Validation(
                "A domain route is addressed by its hostname; listenPort applies to port routes only.");
        }

        // The same rules the certificate machinery applies, at the point the name is typed: a domain a
        // CA would never issue for is worth refusing here rather than discovering as a route that never
        // leaves "pending" (Services/Acme/DesiredHosts.cs).
        if (!DesiredHosts.TryNormalize(command.Domain, out var domain, out var reason))
            return AppError.Validation(reason);

        if (await db.Routes.AnyAsync(r => r.Domain == domain, ct))
            return AppError.Validation($"Domain '{domain}' is already routed.");

        return target == RouteTarget.Watchtower
            ? await CreateWatchtowerRouteAsync(command, domain, ct)
            : await CreateServiceRouteAsync(command, domain, ct);
    }

    private async ValueTask<Result<Response>> CreateServiceRouteAsync(
        Command command, string domain, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(command.ServiceName))
            return AppError.Validation("Service name is required.");
        if (command.ContainerPort is < 1 or > 65535)
            return AppError.Validation("Container port must be between 1 and 65535.");
        if (command.RealmId is not null) {
            return AppError.Validation(
                "A service route takes its realm from its stack's category; realmId applies to Watchtower " +
                "routes only.");
        }

        if (!await db.Stacks.AnyAsync(s => s.Id == command.StackId, ct))
            return AppError.NotFound($"Stack {command.StackId} not found");

        var route = new Route {
            Target = RouteTarget.Service,
            StackId = command.StackId,
            Domain = domain,
            ServiceName = command.ServiceName.Trim(),
            ContainerPort = command.ContainerPort,
            TlsEnabled = command.TlsEnabled,
            IsPrimary = command.IsPrimary,
            Kind = RouteMapping.ParseKind(command.Kind),
            Status = RouteStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);

        await proxy.ConnectStackAsync(command.StackId, ct);
        await proxy.ApplyAsync(ct);

        // Re-read with the stack nav for the DTO.
        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }

    /// <remarks>
    /// <see cref="AccessMode"/> is never taken from the request and is always
    /// <see cref="AccessMode.Public"/>: Watchtower authenticates the visitors of its own surface natively,
    /// so route access control has nothing to add, and a login page behind the gate that redirects to it
    /// would be a closed loop. The check constraint refuses anything else anyway.
    /// </remarks>
    private async ValueTask<Result<Response>> CreateWatchtowerRouteAsync(
        Command command, string domain, CancellationToken ct) {
        // Refused rather than ignored: a caller that filled in a stack and a port has misunderstood what
        // it is creating, and silently dropping those values would produce a route serving something else
        // entirely than the one they described.
        if (command.StackId != 0)
            return AppError.Validation("A Watchtower route has no stack; leave stackId unset.");
        if (!string.IsNullOrWhiteSpace(command.ServiceName) || command.ContainerPort != 0) {
            return AppError.Validation(
                "A Watchtower route forwards nowhere; leave the service name and container port unset.");
        }

        var realmId = command.RealmId ?? Realm.SystemRealmId;
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == realmId, ct);
        if (realm is null)
            return AppError.NotFound($"Realm {realmId} not found.");
        if (RouteMapping.CheckAuthHostCollision(domain, realm, options.CurrentValue.Auth.Host) is { } clash)
            return clash;

        var route = new Route {
            Target = RouteTarget.Watchtower,
            StackId = null,
            RealmId = realm.Id,
            Domain = domain,
            ServiceName = string.Empty,
            ContainerPort = 0,
            TlsEnabled = command.TlsEnabled,
            IsPrimary = command.IsPrimary,
            Kind = RouteMapping.ParseKind(command.Kind),
            AccessMode = AccessMode.Public,
            IdentityHeaderMode = IdentityHeaderMode.None,
            BypassPaths = null,
            Status = RouteStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);

        var makeLoginRoute = command.MakeLoginRoute ?? realm.LoginRouteId is null;
        if (makeLoginRoute) {
            realm.LoginRouteId = route.Id;
            await db.SaveChangesAsync(ct);
        }

        // Which hostnames serve Watchtower has changed, so the generated configuration has. Best-effort,
        // like the service path above.
        await proxy.ApplyAsync(ct);

        var saved = await db.Routes.AsNoTracking()
            .Include(r => r.Realm)
            .FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved, isLoginRoute: makeLoginRoute));
    }

    /// <summary>
    /// Creates a <see cref="RouteBinding.Port"/> route: a stack service on a TLS listener of its own,
    /// with no hostname (ADR-0033).
    /// </summary>
    /// <remarks>
    /// Everything <c>ck_routes_binding</c> makes structural is settled here rather than taken from the
    /// request — the target is a service, the access mode is Public and TLS is on — because each of them
    /// is an invariant the request path relies on rather than a preference. <see cref="Route.IsPrimary"/>
    /// is fixed for a different reason: it says which of a stack's <em>domains</em> is canonical, and a
    /// route with no hostname is not among them. <see cref="Route.Kind"/> is the same kind of value but
    /// is <em>refused</em> rather than fixed, the way <c>proxy.updateRoute</c> refuses it: it is an
    /// optional field, so a caller that filled it in said something about this route, and quietly
    /// storing something else is how the two handlers would end up disagreeing about one request.
    /// </remarks>
    private async ValueTask<Result<Response>> CreatePortRouteAsync(
        Command command, RouteTarget target, CancellationToken ct) {
        if (target != RouteTarget.Service) {
            return AppError.Validation(
                "Watchtower is already served on its management port; a port route forwards to a stack service.");
        }
        if (!string.IsNullOrWhiteSpace(command.Domain)) {
            return AppError.Validation(
                "A port route has no hostname — it is reached by port. Leave the domain empty.");
        }
        if (command.RealmId is not null || command.MakeLoginRoute is not null) {
            return AppError.Validation(
                "A port route serves a stack service, not a realm's Watchtower surface; leave realmId and "
                + "makeLoginRoute unset.");
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

        if (command.ListenPort is not { } listenPort) {
            return AppError.Validation(
                "A port route needs a listen port — the port on this host clients will address it by.");
        }
        var yarp = options.CurrentValue.Proxy.Yarp;
        if (PortRouteRules.ValidateListenPort(listenPort, listener.ManagementPort, yarp) is { } portError)
            return AppError.Validation(portError);
        if (await PortRouteRules.TakenByAsync(db, listenPort, exceptRouteId: null, ct) is { } clash)
            return AppError.Validation(clash);
        // …and the same question asked of the host rather than of the route table: the listener is on
        // Watchtower's own container, so a stack that publishes this port takes it away from us.
        if (await PortRouteRules.PublishedByAnotherContainerAsync(
                docker, listenPort, selfContainerId: null, logger, ct) is { } held)
            return AppError.Validation(held);

        // The certificate for a port route comes from the internal CA and is issued for the LAN names,
        // nothing else — so with none configured there is no name a browser could be pointed at that the
        // certificate would answer for, and the route would come up permanently untrusted.
        if (!PortRouteRules.HasLanNames(yarp))
            return AppError.Validation(PortRouteRules.NoLanNames);

        if (!await db.Stacks.AnyAsync(s => s.Id == command.StackId, ct))
            return AppError.NotFound($"Stack {command.StackId} not found");

        var route = new Route {
            Target = RouteTarget.Service,
            Binding = RouteBinding.Port,
            StackId = command.StackId,
            Domain = null,
            ListenPort = listenPort,
            ServiceName = command.ServiceName.Trim(),
            ContainerPort = command.ContainerPort,
            TlsEnabled = true,
            IsPrimary = false,
            Kind = DomainKind.Managed,
            AccessMode = AccessMode.Public,
            IdentityHeaderMode = IdentityHeaderMode.None,
            BypassPaths = null,
            Status = RouteStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Routes.Add(route);
        try {
            await db.SaveChangesAsync(ct);
        } catch (DbUpdateException ex) when (IsUniqueViolation(ex)) {
            // The check above is the friendly message; this is the same answer under a race, whether with
            // another request on this instance or with a second instance writing against one database.
            return AppError.Conflict(
                $"Port {listenPort} was taken by another route while this one was being created.");
        }

        await proxy.ConnectStackAsync(command.StackId, ct);
        await proxy.ApplyAsync(ct);

        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }

    /// <summary>A write that lost a race on a unique index, as opposed to any other write failure.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
