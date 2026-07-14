using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Networks.Handlers;

/// <summary>
/// The exposure map. Projects every container's port bindings (from <c>GET /containers/json</c>)
/// into a flat list with a server-derived <c>exposure</c>, and groups them into host-port conflicts.
/// Uses containers in ANY state so drift between a running publisher and a stopped/desired one still
/// surfaces as a conflict. When <c>project</c> is set the map is filtered to that compose project.
/// </summary>
[Handler("networks.ports")]
public sealed class ListPublishedPorts(DockerEngineClient docker)
    : IHandler<ListPublishedPorts.Query, Result<ListPublishedPorts.Response>> {
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    public sealed record Query(string? Project);
    public sealed record Response(
        IReadOnlyList<PublishedPortDto> Published,
        IReadOnlyList<PortConflictDto> Conflicts);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        try {
            var containers = await docker.ListAllContainersAsync(ct);

            var published = new List<PublishedPortDto>();
            foreach (var c in containers) {
                var project = c.Labels.TryGetValue(ComposeProjectLabel, out var p) ? p : null;
                if (query.Project is { } filter && !string.Equals(project, filter, StringComparison.Ordinal))
                    continue;

                var containerName = PrimaryName(c.Names);
                var service = c.Labels.TryGetValue(ComposeServiceLabel, out var svc) ? svc : null;
                foreach (var port in c.Ports) {
                    var hostIp = port.IP ?? "";
                    var exposure = DeriveExposure(hostIp, port.PublicPort);
                    published.Add(new PublishedPortDto(
                        c.Id,
                        containerName,
                        project,
                        service,
                        port.PrivatePort,
                        port.PublicPort,
                        NormalizeProtocol(port.Type),
                        hostIp,
                        exposure));
                }
            }

            // Sort by exposure risk (public first), then by public port for stable output.
            published.Sort(CompareByRisk);

            var conflicts = published
                .Where(p => p.PublicPort is not null)
                .GroupBy(p => (p.HostIp, p.PublicPort!.Value, p.Protocol))
                .Where(g => g.Select(p => p.ContainerName).Distinct(StringComparer.Ordinal).Count() >= 2)
                .Select(g => new PortConflictDto(
                    g.Key.Value,
                    g.Key.Protocol,
                    g.Key.HostIp,
                    g.Select(p => p.ContainerName).Distinct(StringComparer.Ordinal).ToList()))
                .ToList();

            return new Response(published, conflicts);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }
    }

    /// <summary>
    /// public = bound to all interfaces (0.0.0.0 / ::); localhost = loopback (127.0.0.1 / ::1);
    /// none = exposed but not published (no host port). Any other specific IP is treated as public
    /// (reachable off-host on that interface).
    /// </summary>
    private static string DeriveExposure(string hostIp, int? publicPort) {
        if (publicPort is null) return "none";
        return hostIp switch {
            "0.0.0.0" or "::" or "" => "public",
            "127.0.0.1" or "::1" => "localhost",
            _ => "public",
        };
    }

    private static string NormalizeProtocol(string? type) =>
        string.IsNullOrEmpty(type) ? "tcp" : type.ToLowerInvariant();

    /// <summary>Sort order: public → localhost → none, then by public port ascending.</summary>
    private static int CompareByRisk(PublishedPortDto a, PublishedPortDto b) {
        var r = RiskRank(a.Exposure).CompareTo(RiskRank(b.Exposure));
        if (r != 0) return r;
        return (a.PublicPort ?? int.MaxValue).CompareTo(b.PublicPort ?? int.MaxValue);
    }

    private static int RiskRank(string exposure) => exposure switch {
        "public" => 0,
        "localhost" => 1,
        _ => 2,
    };

    private static string PrimaryName(string[] names) {
        if (names.Length == 0) return "";
        var n = names[0];
        return n.StartsWith('/') ? n[1..] : n;
    }
}
