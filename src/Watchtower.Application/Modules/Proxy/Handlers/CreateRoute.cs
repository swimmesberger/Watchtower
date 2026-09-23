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
    HostPortOccupancy hostPorts,
    ICurrentUser currentUser,
    TimeProvider time)
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
    /// <param name="AccessMode">
    /// Service domain routes only, and administrators only: the access policy the route starts under.
    /// Left unset (together with <paramref name="BypassPaths"/>) it is the deployment's
    /// <c>Proxy:DefaultAccessMode</c> — <c>Authenticated</c> unless an operator says otherwise (ADR-0035).
    /// </param>
    /// <param name="BypassPaths">
    /// Service domain routes only, and administrators only: newline-separated rooted path prefixes that
    /// stay reachable without signing in — the public webhook or health endpoint of an otherwise gated
    /// app. Stored only for a protected route; a Public one has no access control to except anything from.
    /// </param>
    /// <param name="GrantedUserIds">
    /// <see cref="Entities.AccessMode.Restricted"/> only, administrators only: the accounts let through. With
    /// <paramref name="GrantedGroupIds"/> this is what makes a route creatable Restricted at all — the whole
    /// policy is written in the one transaction that writes the route.
    /// </param>
    /// <param name="GrantedGroupIds">Restricted only, administrators only: the groups let through.</param>
    /// <param name="AccessRuleIds">
    /// <see cref="Entities.AccessMode.Authenticated"/> only, administrators only: the access rules the route
    /// attaches, in precedence order (ADR-0039). None means the instance-wide allow sources.
    /// </param>
    /// <param name="IdentityHeaderMode">
    /// Protected routes only, administrators only: which plaintext identity headers reach the upstream.
    /// Omitted means the JWT only, as on <c>proxy.setAccess</c>.
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
        int? ListenPort = null,
        AccessMode? AccessMode = null,
        string? BypassPaths = null,
        // The rest of a route's access policy, so a create can say everything proxy.setAccess can and a
        // route never has to be published under one policy only to be changed to another a moment later.
        // Optional and last, like every addition to this contract: a client that predates them omits them.
        IReadOnlyList<int>? GrantedUserIds = null,
        IReadOnlyList<int>? GrantedGroupIds = null,
        IReadOnlyList<int>? AccessRuleIds = null,
        IdentityHeaderMode? IdentityHeaderMode = null);

    public sealed record Response(RouteDto Route);

    /// <summary>
    /// Whether the request states anything about access — the trigger for the Admin gate, and for refusing
    /// the request outright on a route whose access is structural. A method rather than a property on the
    /// command so it stays out of the wire contract.
    /// </summary>
    private static bool NamesAccess(Command command) =>
        command.AccessMode is not null || command.BypassPaths is not null
        || command.GrantedUserIds is { Count: > 0 } || command.GrantedGroupIds is { Count: > 0 }
        || command.AccessRuleIds is { Count: > 0 } || command.IdentityHeaderMode is not null;

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

        // The stack first, because its category is the realm every grant and rule in the policy must come
        // from — the route has no row yet to resolve that from.
        var realmId = await RouteAccessValidation.StackRealmIdAsync(db, command.StackId, ct);
        if (realmId is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        var resolved = await ResolveAccessAsync(command, realmId.Value, domain, ct);
        if (resolved.Error is { } accessError)
            return accessError;
        var access = resolved.Access!;

        var route = new Route {
            Target = RouteTarget.Service,
            StackId = command.StackId,
            Domain = domain,
            ServiceName = command.ServiceName.Trim(),
            ContainerPort = command.ContainerPort,
            TlsEnabled = command.TlsEnabled,
            IsPrimary = command.IsPrimary,
            Kind = RouteMapping.ParseKind(command.Kind),
            AccessMode = access.Mode,
            IdentityHeaderMode = access.IdentityHeaderMode,
            BypassPaths = access.BypassPaths,
            Status = RouteStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Routes.Add(route);
        // Through the navigation rather than the id, so the grants and attachments go in with the route in
        // one SaveChanges: the route is never committed — and so never reconciled to the edge — under a
        // policy other than the one asked for, which is what the old create-then-setAccess sequence did.
        foreach (var userId in access.GrantedUserIds)
            db.RouteAccessGrants.Add(new RouteAccessGrant { Route = route, UserId = userId });
        foreach (var groupId in access.GrantedGroupIds)
            db.RouteAccessGrants.Add(new RouteAccessGrant { Route = route, GroupId = groupId });
        var ruleIds = access.AccessRuleIds ?? [];
        for (var i = 0; i < ruleIds.Count; i++)
            db.RouteAccessRules.Add(new RouteAccessRule { Route = route, AccessRuleId = ruleIds[i], Order = i });
        await db.SaveChangesAsync(ct);

        await proxy.ConnectStackAsync(command.StackId, ct);
        await proxy.ApplyAsync(ct);

        // An explicitly chosen policy is an access decision, and leaves the trace proxy.setAccess would have
        // left for the same decision made afterwards. A create that said nothing took the configured default,
        // which is a setting and was audited when it was set.
        if (NamesAccess(command))
            await RouteAccessAudit.RecordAsync(db, currentUser, time, route, access.Mode, "on create");

        // Re-read with the stack nav for the DTO.
        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }

    /// <remarks>
    /// <see cref="Route.AccessMode"/> is never taken from the request and is always
    /// <see cref="AccessMode.Public"/> — the deployment's protected-by-default setting included
    /// (ADR-0035): Watchtower authenticates the visitors of its own surface natively, so route access
    /// control has nothing to add, and a login page behind the gate that redirects to it would be a
    /// closed loop. The check constraint refuses anything else anyway. A request that <em>asked</em> for
    /// a policy is refused rather than quietly given Public, for the same reason a stack is.
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
        if (RefuseAccessFields(
                command,
                "Watchtower routes use Watchtower's own login; route access control does not apply — "
                + "leave the access fields unset.") is { } accessError) {
            return accessError;
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
        if (RefuseAccessFields(
                command,
                "A port route is always public. It has no hostname for a login redirect to return to, so "
                + "route access control does not apply — leave the access fields unset.")
            is { } accessError) {
            return accessError;
        }
        if (string.IsNullOrWhiteSpace(command.ServiceName))
            return AppError.Validation("Service name is required.");
        if (command.ContainerPort is < 1 or > 65535)
            return AppError.Validation("Container port must be between 1 and 65535.");

        if (command.ListenPort is not { } listenPort) {
            return AppError.Validation(
                "A port route needs a listen port — the port on this host clients will address it by.");
        }
        var proxyOptions = options.CurrentValue.Proxy;
        var yarp = proxyOptions.Yarp;
        if (PortRouteRules.ValidateListenPort(listenPort, listener.ManagementPort, yarp) is { } portError)
            return AppError.Validation(portError);
        if (await PortRouteRules.TakenByAsync(db, listenPort, exceptRouteId: null, ct) is { } clash)
            return AppError.Validation(clash);
        // …and the same question asked of the host rather than of the route table: the listener is on
        // Watchtower's own container, so a stack that publishes this port takes it away from us.
        if (await hostPorts.PublishedByAnotherContainerAsync(
                listenPort, selfContainerId: null, ct) is { } held)
            return AppError.Validation(held);

        // The certificate for a port route comes from the internal CA and is issued for the LAN names,
        // nothing else — so with none configured there is no name a browser could be pointed at that the
        // certificate would answer for, and the route would come up permanently untrusted.
        if (!PortRouteRules.HasLanNames(proxyOptions.PortRoutes))
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

    /// <summary>
    /// Settles the whole access policy a new service route starts under (ADR-0035, ADR-0039): the mode,
    /// bypass paths, identity forwarding, and the grants or access rules that decide who the mode admits.
    /// </summary>
    /// <remarks>
    /// Two paths, and the split is the point. A request that says nothing about access gets the
    /// deployment's default — <c>Authenticated</c> unless an operator changed it — which is what makes a
    /// freshly published hostname gated rather than open to the internet. A request that says
    /// <em>anything</em> about access is an administrative act and needs the Admin role, the same one
    /// <c>proxy.setAccess</c> demands: the gate fires on any explicit value rather than on a "non-default"
    /// one, because the default can change between the moment a form is rendered and the moment it is
    /// submitted, and a rule nobody can predict is not a rule.
    /// <para>
    /// Everything else is <see cref="RouteAccessValidation"/>, the validation <c>proxy.setAccess</c> runs, so
    /// a route can be created with exactly the policies it could later be changed to. The create used to be
    /// narrower — no grants, no rules, and Restricted refused "because a create carries no grants" — which
    /// forced a second step that published the route under the instance-wide allow-list first.
    /// </para>
    /// <para>
    /// Two refusals are the create's own. A Restricted route naming nobody is refused, because publishing a
    /// new hostname that admits no one reads as a broken deployment rather than as a policy (an existing
    /// route may still be narrowed to nobody through <c>setAccess</c>, where it is a deliberate lockout). And
    /// under Cloudflare, an Authenticated route with no rules is refused while no instance-wide allow source
    /// exists, because that is the list it would be projected from — a route attaching rules is judged on
    /// its rules instead (ADR-0039 decision 5), and a Restricted one on its grants.
    /// </para>
    /// </remarks>
    private async Task<RouteAccessValidation.Outcome> ResolveAccessAsync(
        Command command, int realmId, string domain, CancellationToken ct) {
        var proxyOptions = options.CurrentValue.Proxy;

        if (NamesAccess(command) && !currentUser.IsInRole(WatchtowerClaims.AdminRole)) {
            return new RouteAccessValidation.Outcome(
                AppError.Forbidden(
                    "Only an administrator can choose a route's access policy. Leave the access fields unset "
                    + "to create the route under the configured default."),
                null);
        }

        var mode = command.AccessMode ?? proxyOptions.ResolveDefaultAccessMode();
        var outcome = await RouteAccessValidation.ValidateAsync(
            db,
            new RouteAccessRequest(
                mode, command.BypassPaths, command.GrantedUserIds, command.GrantedGroupIds,
                command.AccessRuleIds, command.IdentityHeaderMode),
            realmId,
            domain,
            AccessClauseSupport.PointFor(proxyOptions.ResolveProvider()),
            ct);
        if (outcome.Error is not null) return outcome;
        var access = outcome.Access!;

        if (access.Mode == AccessMode.Restricted
            && access.GrantedUserIds.Count == 0 && access.GrantedGroupIds.Count == 0) {
            return new RouteAccessValidation.Outcome(
                AppError.Validation(
                    "A new Restricted route has to name at least one user or group — otherwise nobody would be "
                    + "admitted. Pick them here, or create it authenticated."),
                null);
        }

        // Under Cloudflare the gate is an Access application, and an application nobody can pass is a route
        // the edge denies outright (ADR-0035). Only the one case whose allow-list *is* the instance-wide
        // settings is checked against them — better said here, while the operator is looking, than
        // discovered as a route that answers 403 to everyone.
        if (access.Mode == AccessMode.Authenticated
            && access.AccessRuleIds is not { Count: > 0 }
            && proxyOptions.Enabled
            && proxyOptions.ResolveProvider() == ProxyProviderKind.Cloudflare
            && !proxyOptions.Cloudflare.HasAccessAllowSource()) {
            return new RouteAccessValidation.Outcome(
                AppError.Validation(
                    "Cloudflare Zero Trust has no allow source configured, so a protected route would deny "
                    + "everyone. Attach an access rule, add allowed emails, email domains, an Access group id or "
                    + "a reusable policy id under Settings → Reverse proxy, or create this route as public."),
                null);
        }

        return outcome;
    }

    /// <summary>
    /// Refuses a request that names an access policy on a route whose mode is structural, returning the
    /// operator-facing message or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Refused rather than ignored, the same house rule the stack and service fields follow above: an
    /// administrator who thought they had gated a hostname must find out that they have not. Both check
    /// constraints (<c>ck_routes_target</c>, <c>ck_routes_binding</c>) would refuse the row anyway — as a
    /// 500 naming a constraint instead of a sentence.
    /// </remarks>
    private static AppError? RefuseAccessFields(Command command, string reason) =>
        NamesAccess(command) ? AppError.Validation(reason) : null;

    /// <summary>A write that lost a race on a unique index, as opposed to any other write failure.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
