using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Networks.Handlers;

/// <summary>
/// Lists Docker networks (cheap enough to poll on the container backoff). Each network is inspected
/// (<c>GET /networks/{id}</c>) for its attached containers; each endpoint's stack name is resolved
/// from the container's <c>com.docker.compose.project</c> label. Ref-count and the three-state
/// lifecycle are computed server-side. When <c>project</c> is set, the list is filtered to that
/// compose project (docker default networks are always included, since they carry no project label
/// but are host-wide context).
/// </summary>
[Handler("networks.list")]
public sealed class ListNetworks(DockerEngineClient docker)
    : IHandler<ListNetworks.Query, Result<ListNetworks.Response>> {
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeNetworkLabel = "com.docker.compose.network";

    /// <summary>Docker's built-in networks, which are never orphaned and never deleted.</summary>
    private static readonly HashSet<string> DefaultNetworks =
        new(StringComparer.Ordinal) { "bridge", "host", "none" };

    public sealed record Query(string? Project);
    public sealed record Response(IReadOnlyList<NetworkDto> Networks);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        try {
            var networks = await docker.ListNetworksAsync(ct);
            // Resolve stack names for attached containers from their compose labels.
            var containers = await docker.ListAllContainersAsync(ct);
            var projectById = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var c in containers)
                projectById[c.Id] = c.Labels.TryGetValue(ComposeProjectLabel, out var p) ? p : null;

            var items = new List<NetworkDto>(networks.Count);
            foreach (var n in networks) {
                IReadOnlyDictionary<string, string> labels = n.Labels ?? new Dictionary<string, string>();
                var project = labels.TryGetValue(ComposeProjectLabel, out var proj) ? proj : null;
                var isDefault = DefaultNetworks.Contains(n.Name);

                // Project scope: keep the stack's own networks; a default network only counts
                // when one of the stack's containers is actually attached to it (checked below,
                // after inspect resolves attachments).
                if (query.Project is { } filter && !isDefault
                    && !string.Equals(project, filter, StringComparison.Ordinal))
                    continue;

                var composeNetwork = labels.TryGetValue(ComposeNetworkLabel, out var cn) ? cn : null;

                // The list endpoint does not populate Containers — inspect for attachment.
                var detail = await docker.InspectNetworkAsync(n.Id, ct);
                var attached = new List<NetworkEndpointDto>();
                if (detail.Containers is not null) {
                    foreach (var (containerId, ep) in detail.Containers) {
                        var stackName = projectById.TryGetValue(containerId, out var sp) ? sp : null;
                        attached.Add(new NetworkEndpointDto(
                            containerId,
                            ep.Name ?? "",
                            stackName,
                            string.IsNullOrEmpty(ep.IPv4Address) ? null : ep.IPv4Address,
                            string.IsNullOrEmpty(ep.IPv6Address) ? null : ep.IPv6Address));
                    }
                }

                var ipamConfig = detail.IPAM?.Config?.FirstOrDefault();
                var ipam = new NetworkIpamDto(ipamConfig?.Subnet, ipamConfig?.Gateway);

                // Default networks pass the label filter above unconditionally; in project scope
                // they are only relevant when the stack actually has a container attached.
                if (query.Project is { } scopedFilter && isDefault
                    && !string.Equals(project, scopedFilter, StringComparison.Ordinal)
                    && !attached.Any(a => string.Equals(a.StackName, scopedFilter, StringComparison.Ordinal)))
                    continue;

                var refCount = attached.Count;
                var lifecycle = ResolveLifecycle(project, refCount, isDefault);

                items.Add(new NetworkDto(
                    detail.Id,
                    detail.Name,
                    detail.Driver,
                    detail.Scope,
                    detail.Internal,
                    project,
                    composeNetwork,
                    detail.CreatedAt,
                    labels,
                    ipam,
                    attached,
                    refCount,
                    lifecycle,
                    isDefault));
            }

            return new Response(items);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }
    }

    /// <summary>
    /// live = attached; declared = compose-labelled but unattached; orphaned = no label + unattached.
    /// Docker defaults are treated as live regardless of attachment (they are never orphaned).
    /// </summary>
    private static string ResolveLifecycle(string? project, int refCount, bool isDefault) {
        if (isDefault || refCount > 0) return "live";
        return project is not null ? "declared" : "orphaned";
    }
}
