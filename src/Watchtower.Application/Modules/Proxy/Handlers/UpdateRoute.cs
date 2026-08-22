using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Updates a route's domain/target/TLS settings, then reconciles the proxy.</summary>
[Handler("proxy.updateRoute")]
public sealed class UpdateRoute(WatchtowerDbContext db, IProxyProvider proxy)
    : IHandler<UpdateRoute.Command, Result<UpdateRoute.Response>> {
    public sealed record Command(
        int Id,
        string Domain,
        string ServiceName,
        int ContainerPort,
        bool TlsEnabled,
        bool IsPrimary);

    public sealed record Response(RouteDto Route);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        // The same rules the certificate machinery applies, at the point the name is typed: a domain a
        // CA would never issue for is worth refusing here rather than discovering as a route that never
        // leaves "pending" (Services/Acme/DesiredHosts.cs).
        if (!DesiredHosts.TryNormalize(command.Domain, out var domain, out var reason))
            return AppError.Validation(reason);
        if (string.IsNullOrWhiteSpace(command.ServiceName))
            return AppError.Validation("Service name is required.");
        if (command.ContainerPort is < 1 or > 65535)
            return AppError.Validation("Container port must be between 1 and 65535.");

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (route is null)
            return AppError.NotFound($"Route {command.Id} not found");
        if (await db.Routes.AnyAsync(r => r.Domain == domain && r.Id != command.Id, ct))
            return AppError.Validation($"Domain '{domain}' is already routed.");

        route.Domain = domain;
        route.ServiceName = command.ServiceName.Trim();
        route.ContainerPort = command.ContainerPort;
        route.TlsEnabled = command.TlsEnabled;
        route.IsPrimary = command.IsPrimary;
        await db.SaveChangesAsync(ct);

        await proxy.ConnectStackAsync(route.StackId, ct);
        await proxy.ApplyAsync(ct);

        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }
}
