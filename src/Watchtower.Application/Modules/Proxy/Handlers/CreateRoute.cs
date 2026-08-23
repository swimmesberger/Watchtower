using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Creates a route. A <see cref="RouteTarget.Service"/> route (the default) maps a domain to a service
/// inside a stack: it is persisted, the target service container is joined to the edge network, and the
/// proxy is reloaded. A <see cref="RouteTarget.Watchtower"/> route maps a domain to <em>this instance</em>
/// (ADR-0023) — no stack, no upstream, and optionally the realm's login host. The proxy work is a no-op
/// when the reverse proxy is disabled.
/// </summary>
[Handler("proxy.createRoute")]
public sealed class CreateRoute(
    WatchtowerDbContext db, IProxyProvider proxy, IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<CreateRoute.Command, Result<CreateRoute.Response>> {
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
    public sealed record Command(
        int StackId,
        string Domain,
        string ServiceName,
        int ContainerPort,
        bool TlsEnabled,
        bool IsPrimary,
        string? Kind,
        string? Target = null,
        int? RealmId = null,
        bool? MakeLoginRoute = null);

    public sealed record Response(RouteDto Route);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);

        // The same rules the certificate machinery applies, at the point the name is typed: a domain a
        // CA would never issue for is worth refusing here rather than discovering as a route that never
        // leaves "pending" (Services/Acme/DesiredHosts.cs).
        if (!DesiredHosts.TryNormalize(command.Domain, out var domain, out var reason))
            return AppError.Validation(reason);
        if (!RouteMapping.TryParseTarget(command.Target, out var target))
            return AppError.Validation($"Unknown route target '{command.Target}'. Use 'service' or 'watchtower'.");

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
}
