using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Lists the account's <b>foreign</b> public hostnames — ingress rules configured in the Cloudflare
/// dashboard (before or beside Watchtower) whose hostname is not in the route table. Every
/// non-deleted tunnel is scanned, not just Watchtower's own: pre-existing applications typically
/// live on a tunnel the operator created in the dashboard, and Watchtower's tunnel only exists after
/// its first reconcile. The reconcile preserves foreign rules on its own tunnel verbatim
/// (merge-don't-replace); this handler is the read side of adopting any of them: the Routes page
/// shows each one with a heuristic stack/service/port suggestion, and importing is simply
/// <c>proxy.createRoute</c> with the prefilled values — after which the route table owns the
/// hostname and the next reconcile serves it from Watchtower's tunnel (repointing its DNS there).
/// </summary>
/// <remarks>
/// The suggestion heuristic (<see cref="Suggest"/>) recognizes the alias convention Watchtower itself
/// writes (<c>http://{composeProject}-{service}:{port}</c>, see
/// <see cref="ProxyIngressNetworks.EdgeAlias"/>) by longest matching compose-project prefix. Services
/// pointing anywhere else (IPs, localhost ports, other schemes) get no suggestion — the operator maps
/// those by hand in the dialog. Returns an empty list when the cloudflare provider is not active or
/// not configured ("not applicable" looks like "nothing to import" there), but states WHY the list is
/// empty via <see cref="Response.Warning"/> when the provider IS active and the tunnel cannot be seen —
/// a missing tunnel or an empty remote configuration would otherwise be indistinguishable from a
/// healthy tunnel with nothing foreign on it, which reads as "my routes are not showing up".
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
        string TunnelName,
        int? SuggestedStackId,
        string? SuggestedStackName,
        string? SuggestedServiceName,
        int? SuggestedContainerPort);

    /// <param name="Routes">The importable foreign hostnames; empty when there are none.</param>
    /// <param name="Warning">
    /// Why the list is empty when that emptiness is suspicious — the configured tunnel does not exist
    /// (yet), or its remote configuration carries no ingress at all (e.g. a locally-managed tunnel
    /// whose hostnames live in a cloudflared config file Watchtower cannot read). Null when the
    /// provider is not active or the tunnel was read normally.
    /// </param>
    public sealed record Response(IReadOnlyList<ForeignRouteDto> Routes, string? Warning = null);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        var cf = proxy.Cloudflare;
        if (!proxy.Enabled
            || proxy.ResolveProvider() != ProxyProviderKind.Cloudflare
            || string.IsNullOrWhiteSpace(cf.AccountId)
            || string.IsNullOrWhiteSpace(cf.ApiToken)
            || string.IsNullOrWhiteSpace(cf.TunnelName))
            return new Response([]);

        IReadOnlyList<CloudflareTunnel> tunnels;
        var rulesByTunnel = new List<(string TunnelName, IReadOnlyList<CloudflareIngressRule> Rules)>();
        try {
            tunnels = await api.ListTunnelsAsync(cf.AccountId!, cf.ApiToken!, ct);
            foreach (var tunnel in tunnels)
                rulesByTunnel.Add((tunnel.Name, await api.GetTunnelConfigurationAsync(cf.AccountId!, tunnel.Id, cf.ApiToken!, ct)));
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Could not read the tunnel configuration from Cloudflare: {ex.Message}");
        }

        if (tunnels.Count == 0)
            return new Response([],
                "The Cloudflare account has no tunnels yet — Watchtower's is created on the first "
                + "reconcile after the provider is enabled.");

        if (rulesByTunnel.All(t => t.Rules.Count == 0))
            return new Response([],
                "None of the account's tunnels carry a remote configuration. Hostnames published from "
                + "a locally-managed cloudflared configuration file are not visible to Watchtower — "
                + "recreate them as remotely-managed public hostnames, or add them here as routes.");

        var routeDomains = await db.Routes.AsNoTracking().Select(r => r.Domain).ToListAsync(ct);
        var stacks = await db.Stacks.AsNoTracking()
            .Select(s => new StackCandidate(s.Id, s.Name, s.ComposeProjectName))
            .ToListAsync(ct);

        var routes = rulesByTunnel
            .SelectMany(t => CloudflareTunnelProvider.ForeignIngressRules(t.Rules, routeDomains)
                .Select(rule => {
                    var suggestion = Suggest(rule.Service, stacks);
                    return new ForeignRouteDto(
                        rule.Hostname!,
                        rule.Service,
                        rule.Path,
                        t.TunnelName,
                        suggestion?.StackId,
                        suggestion?.StackName,
                        suggestion?.ServiceName,
                        suggestion?.ContainerPort);
                }))
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
