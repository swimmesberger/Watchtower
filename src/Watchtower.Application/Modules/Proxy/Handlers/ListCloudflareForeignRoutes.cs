using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Lists the tunnel's <b>foreign</b> public hostnames — ingress rules configured in the Cloudflare
/// dashboard (before or beside Watchtower) whose hostname is not in the route table. The reconcile
/// preserves them verbatim (merge-don't-replace); this handler is the read side of adopting them:
/// the Routes page shows each one with a heuristic stack/service/port suggestion, and importing is
/// simply <c>proxy.createRoute</c> with the prefilled values — after which the hostname stops being
/// foreign and the route table owns it.
/// </summary>
/// <remarks>
/// The suggestion heuristic (<see cref="Suggest"/>) recognizes the alias convention Watchtower itself
/// writes (<c>http://{composeProject}-{service}:{port}</c>, see
/// <see cref="ProxyIngressNetworks.EdgeAlias"/>) by longest matching compose-project prefix. Services
/// pointing anywhere else (IPs, localhost ports, other schemes) get no suggestion — the operator maps
/// those by hand in the dialog. Returns an empty list whenever the cloudflare provider is not active
/// or not configured — "nothing to import" and "not applicable" look the same to the UI on purpose.
/// </remarks>
[Handler("proxy.listCloudflareForeignRoutes")]
public sealed class ListCloudflareForeignRoutes(
    WatchtowerDbContext db,
    CloudflareApiClient api,
    IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<ListCloudflareForeignRoutes.Query, Result<ListCloudflareForeignRoutes.Response>> {
    public sealed record Query;

    /// <summary>One dashboard-made hostname, with the best-effort mapping onto a stack service.</summary>
    public sealed record ForeignRouteDto(
        string Hostname,
        string Service,
        string? Path,
        int? SuggestedStackId,
        string? SuggestedStackName,
        string? SuggestedServiceName,
        int? SuggestedContainerPort);

    public sealed record Response(IReadOnlyList<ForeignRouteDto> Routes);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        var cf = proxy.Cloudflare;
        if (!proxy.Enabled
            || proxy.ResolveProvider() != ProxyProviderKind.Cloudflare
            || string.IsNullOrWhiteSpace(cf.AccountId)
            || string.IsNullOrWhiteSpace(cf.ApiToken)
            || string.IsNullOrWhiteSpace(cf.TunnelName))
            return new Response([]);

        CloudflareTunnel? tunnel;
        IReadOnlyList<CloudflareIngressRule> existing;
        try {
            tunnel = await api.FindTunnelAsync(cf.AccountId!, cf.TunnelName, cf.ApiToken!, ct);
            if (tunnel is null) return new Response([]);
            existing = await api.GetTunnelConfigurationAsync(cf.AccountId!, tunnel.Id, cf.ApiToken!, ct);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Could not read the tunnel configuration from Cloudflare: {ex.Message}");
        }

        var routeDomains = await db.Routes.AsNoTracking().Select(r => r.Domain).ToListAsync(ct);
        var foreign = CloudflareTunnelProvider.ForeignIngressRules(existing, routeDomains);
        if (foreign.Count == 0) return new Response([]);

        var stacks = await db.Stacks.AsNoTracking()
            .Select(s => new StackCandidate(s.Id, s.Name, s.ComposeProjectName))
            .ToListAsync(ct);

        var routes = foreign
            .Select(rule => {
                var suggestion = Suggest(rule.Service, stacks);
                return new ForeignRouteDto(
                    rule.Hostname!,
                    rule.Service,
                    rule.Path,
                    suggestion?.StackId,
                    suggestion?.StackName,
                    suggestion?.ServiceName,
                    suggestion?.ContainerPort);
            })
            .OrderBy(r => r.Hostname, StringComparer.Ordinal)
            .ToList();
        return new Response(routes);
    }

    /// <summary>A stack the suggestion heuristic may map a service URL onto.</summary>
    internal sealed record StackCandidate(int Id, string Name, string ComposeProjectName);

    internal sealed record Suggestion(int StackId, string StackName, string ServiceName, int ContainerPort);

    /// <summary>
    /// Maps an ingress service URL onto a stack service when it follows Watchtower's own alias
    /// convention: <c>http(s)://{composeProject}-{service}:{port}</c>. Compose project names may
    /// themselves contain dashes, so the match is by <b>longest</b> project prefix; the remainder is
    /// the service name. Anything else — IPs, localhost, bare hosts without a matching project,
    /// non-http schemes — yields null and is mapped by hand.
    /// </summary>
    internal static Suggestion? Suggest(string service, IReadOnlyList<StackCandidate> stacks) {
        if (!Uri.TryCreate(service, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host;
        var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;

        StackCandidate? best = null;
        string? bestService = null;
        foreach (var stack in stacks) {
            var prefix = stack.ComposeProjectName + "-";
            if (!host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var serviceName = host[prefix.Length..];
            if (serviceName.Length == 0) continue;
            if (best is null || stack.ComposeProjectName.Length > best.ComposeProjectName.Length) {
                best = stack;
                bestService = serviceName;
            }
        }
        return best is null ? null : new Suggestion(best.Id, best.Name, bestService!, port);
    }
}
