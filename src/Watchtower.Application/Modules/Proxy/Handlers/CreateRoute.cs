using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Creates a route (domain → service). Persists it, joins the target service container to the edge
/// network, and reloads the proxy. The proxy work is a no-op when the reverse proxy is disabled.
/// </summary>
[Handler("proxy.createRoute")]
public sealed class CreateRoute(WatchtowerDbContext db, CaddyManager caddy)
    : IHandler<CreateRoute.Command, Result<CreateRoute.Response>> {
    public sealed record Command(
        int StackId,
        string Domain,
        string ServiceName,
        int ContainerPort,
        bool TlsEnabled,
        bool IsPrimary,
        string? Kind);

    public sealed record Response(RouteDto Route);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var domain = RouteMapping.NormalizeDomain(command.Domain);
        if (domain is null)
            return AppError.Validation("Domain is required.");
        if (string.IsNullOrWhiteSpace(command.ServiceName))
            return AppError.Validation("Service name is required.");
        if (command.ContainerPort is < 1 or > 65535)
            return AppError.Validation("Container port must be between 1 and 65535.");

        if (!await db.Stacks.AnyAsync(s => s.Id == command.StackId, ct))
            return AppError.NotFound($"Stack {command.StackId} not found");
        if (await db.Routes.AnyAsync(r => r.Domain == domain, ct))
            return AppError.Validation($"Domain '{domain}' is already routed.");

        var route = new Route {
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

        await caddy.ConnectStackAsync(command.StackId, ct);
        await caddy.ApplyAsync(ct);

        // Re-read with the stack nav for the DTO.
        var saved = await db.Routes.AsNoTracking().Include(r => r.Stack).FirstAsync(r => r.Id == route.Id, ct);
        return new Response(RouteMapping.ToDto(saved));
    }
}
