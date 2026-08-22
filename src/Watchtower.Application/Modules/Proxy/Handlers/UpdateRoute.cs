using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Updates a route's domain/target/TLS settings, then reconciles the proxy.</summary>
/// <remarks>
/// <see cref="Route.Target"/> is <b>not</b> editable (ADR-0021). The two kinds are different rows with
/// different required columns, and flipping one in place would silently re-point a live hostname at
/// something else — delete and recreate says what is happening.
/// </remarks>
[Handler("proxy.updateRoute")]
public sealed class UpdateRoute(
    WatchtowerDbContext db, IProxyProvider proxy, IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<UpdateRoute.Command, Result<UpdateRoute.Response>> {
    /// <param name="MakeLoginRoute">
    /// Watchtower routes only: <see langword="true"/> designates this route as its realm's login host,
    /// <see langword="false"/> clears the designation if this route currently holds it, and omitting it
    /// leaves the realm alone.
    /// </param>
    public sealed record Command(
        int Id,
        string Domain,
        string ServiceName,
        int ContainerPort,
        bool TlsEnabled,
        bool IsPrimary,
        string? Kind = null,
        bool? MakeLoginRoute = null);

    public sealed record Response(RouteDto Route);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);

        // The same rules the certificate machinery applies, at the point the name is typed: a domain a
        // CA would never issue for is worth refusing here rather than discovering as a route that never
        // leaves "pending" (Services/Acme/DesiredHosts.cs).
        if (!DesiredHosts.TryNormalize(command.Domain, out var domain, out var reason))
            return AppError.Validation(reason);

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (route is null)
            return AppError.NotFound($"Route {command.Id} not found");
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

        route.Domain = domain;
        route.ServiceName = command.ServiceName.Trim();
        route.ContainerPort = command.ContainerPort;
        route.TlsEnabled = command.TlsEnabled;
        route.IsPrimary = command.IsPrimary;
        if (command.Kind is not null) route.Kind = RouteMapping.ParseKind(command.Kind);
        await db.SaveChangesAsync(ct);

        // StackId is non-null on a service route by the check constraint; the compiler cannot see that.
        await proxy.ConnectStackAsync(route.StackId!.Value, ct);
        await proxy.ApplyAsync(ct);

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
}
