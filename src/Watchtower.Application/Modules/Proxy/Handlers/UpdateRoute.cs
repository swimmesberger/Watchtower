using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Updates a route's domain/target/TLS settings, then reconciles the proxy.</summary>
[Handler("proxy.updateRoute")]
public sealed class UpdateRoute(WatchtowerDbContext db, CaddyManager caddy)
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
        var domain = RouteMapping.NormalizeDomain(command.Domain);
        if (domain is null)
            return AppError.Validation("Domain is required.");
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

        await caddy.ConnectStackAsync(route.StackId, ct);
        await caddy.ApplyAsync(ct);

        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }
}
