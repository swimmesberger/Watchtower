using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Deletes a route and reloads the proxy so it stops serving the domain. By default the hostname is
/// merely <em>unowned</em>: whatever the provider holds for it outside Watchtower (a tunnel ingress rule,
/// a DNS record) is left as it is — the next reconcile preserves it as foreign, and it shows up again as
/// importable. With <see cref="Command.RemoveFromProvider"/> the provider also removes what Watchtower
/// wrote for the hostname, so the domain is gone end to end.
/// </summary>
[Handler("proxy.deleteRoute")]
public sealed class DeleteRoute(WatchtowerDbContext db, IProxyProvider proxy, AuditLog audit, ICurrentUser currentUser)
    : IHandler<DeleteRoute.Command, Result<DeleteRoute.Response>> {
    /// <param name="RemoveFromProvider">
    /// Also remove the hostname from the provider's control plane (Cloudflare: the tunnel ingress rule
    /// and the CNAME Watchtower pointed at the tunnel). Ignored by providers with nothing external to
    /// remove (Caddy regenerates its whole configuration from the table).
    /// </param>
    public sealed record Command(int Id, bool RemoveFromProvider = false);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var domain = await db.Routes.AsNoTracking()
            .Where(r => r.Id == command.Id)
            .Select(r => r.Domain)
            .FirstOrDefaultAsync(ct);
        if (domain is null)
            return AppError.NotFound($"Route {command.Id} not found");

        await db.Routes.Where(r => r.Id == command.Id).ExecuteDeleteAsync(ct);

        // Forget BEFORE the reconcile: the reconcile preserves every rule for a hostname not in the
        // table as foreign, so removing the rule afterwards would have to fight it.
        string? cleanupError = null;
        if (command.RemoveFromProvider) {
            try {
                await proxy.ForgetDomainAsync(domain, await audit.ActorAsync(currentUser, ct), ct);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                cleanupError = ex.Message;
            }
        }
        await proxy.ApplyAsync(ct);

        // The route is gone either way; a failed provider cleanup is reported rather than rolled back —
        // the audit trail carries the provider's words, and the hostname is still visible as foreign.
        return cleanupError is null
            ? new Response(command.Id)
            : AppError.Internal($"The route was deleted, but removing {domain} from the provider failed: {cleanupError}");
    }
}
