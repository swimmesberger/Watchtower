using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The Docker network topology both proxy providers share (extracted from <see cref="CaddyManager"/>
/// for ADR-0015): one private ingress network per stack, joined by the proxy container and that
/// stack's routed service containers under a stable DNS alias. Tenants stay isolated at L2 — a
/// compromised tenant cannot reach another tenant's containers — and the proxy reaches every upstream
/// as <c>{project}-{service}:{port}</c> regardless of which provider is active.
/// </summary>
public sealed class ProxyIngressNetworks(
    IServiceScopeFactory scopeFactory,
    DockerEngineClient docker,
    ILogger<ProxyIngressNetworks> logger) {
    public const string IngressNetworkPrefix = "watchtower-ingress-";
    public const string ManagedLabelKey = "com.watchtower.managed";
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeServiceLabel = "com.docker.compose.service";

    /// <summary>Stable, collision-free DNS alias for a service on its ingress network (unique per stack).</summary>
    public static string EdgeAlias(string project, string service) => $"{project}-{service}";

    /// <summary>Creates a named network with the managed label if it does not exist yet.</summary>
    public async Task EnsureNetworkAsync(string name, string labelValue, CancellationToken ct) {
        var networks = await docker.ListNetworksAsync(ct);
        if (networks.Any(n => n.Name == name)) return;
        logger.LogInformation("Creating proxy network {Network}", name);
        await docker.CreateNetworkAsync(name, new Dictionary<string, string> { [ManagedLabelKey] = labelValue }, ct);
    }

    /// <summary>
    /// Ensures the stack's ingress network exists and the proxy container is on it; returns its name.
    /// Idempotent: a 403 (already connected) is treated as success by <c>ConnectContainerAsync</c>.
    /// </summary>
    public async Task<string> EnsureStackNetworkAsync(int stackId, string proxyContainer, CancellationToken ct) {
        var network = IngressNetworkPrefix + stackId;
        await EnsureNetworkAsync(network, "ingress", ct);
        await docker.ConnectContainerAsync(network, proxyContainer, aliases: null, ct);
        return network;
    }

    /// <summary>
    /// Ensures the stack's ingress network (with the proxy on it), then connects every container of a
    /// compose service to it under the stable alias.
    /// </summary>
    public async Task ConnectServiceAsync(
        int stackId, string project, string service, string proxyContainer, CancellationToken ct) {
        var network = await EnsureStackNetworkAsync(stackId, proxyContainer, ct);
        var alias = EdgeAlias(project, service);
        var containers = await docker.ListContainersByLabelsAsync(
            [$"{ComposeProjectLabel}={project}", $"{ComposeServiceLabel}={service}"], ct);
        if (containers.Count == 0) {
            logger.LogDebug("No container found for {Project}/{Service}; nothing to connect yet.", project, service);
            return;
        }
        foreach (var c in containers) {
            try {
                await docker.ConnectContainerAsync(network, c.Id, [alias], ct);
            } catch (Exception ex) {
                var shortId = c.Id.Length >= 12 ? c.Id[..12] : c.Id;
                logger.LogWarning(ex, "Failed to connect {Container} ({Alias}) to {Network}", shortId, alias, network);
            }
        }
    }

    /// <summary>Connects one stack's routed services (from the route table) to its ingress network.</summary>
    public async Task ConnectStackServicesAsync(int stackId, string proxyContainer, CancellationToken ct) {
        List<(string Project, string Service)> targets;
        await using (var scope = scopeFactory.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            targets = await db.Routes.AsNoTracking()
                .Where(r => r.StackId == stackId)
                .Include(r => r.Stack)
                .Select(r => new { r.Stack!.ComposeProjectName, r.ServiceName })
                .Distinct()
                .Select(x => new ValueTuple<string, string>(x.ComposeProjectName, x.ServiceName))
                .ToListAsync(ct);
        }
        foreach (var (project, service) in targets)
            await ConnectServiceAsync(stackId, project, service, proxyContainer, ct);
    }

    /// <summary>Connects every routed service of every stack — the startup-reconcile sweep.</summary>
    public async Task ConnectAllRoutedContainersAsync(string proxyContainer, CancellationToken ct) {
        List<(int StackId, string Project, string Service)> targets;
        await using (var scope = scopeFactory.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            targets = await db.Routes.AsNoTracking()
                .Include(r => r.Stack)
                .Select(r => new { r.StackId, r.Stack!.ComposeProjectName, r.ServiceName })
                .Distinct()
                .Select(x => new ValueTuple<int, string, string>(x.StackId, x.ComposeProjectName, x.ServiceName))
                .ToListAsync(ct);
        }
        foreach (var (stackId, project, service) in targets)
            await ConnectServiceAsync(stackId, project, service, proxyContainer, ct);
    }
}
