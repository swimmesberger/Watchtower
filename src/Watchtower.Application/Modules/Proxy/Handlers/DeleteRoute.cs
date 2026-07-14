using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Deletes a route and reloads the proxy so it stops serving the domain.</summary>
[Handler("proxy.deleteRoute")]
public sealed class DeleteRoute(WatchtowerDbContext db, CaddyManager caddy)
    : IHandler<DeleteRoute.Command, Result<DeleteRoute.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var deleted = await db.Routes.Where(r => r.Id == command.Id).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            return AppError.NotFound($"Route {command.Id} not found");

        await caddy.ApplyAsync(ct);
        return new Response(command.Id);
    }
}
