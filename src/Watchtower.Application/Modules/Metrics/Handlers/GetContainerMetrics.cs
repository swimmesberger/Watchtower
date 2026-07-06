using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Returns per-container CPU/memory readouts (with history) from the active <see cref="IMetricsSource"/>
/// (ADR-0007) — the in-memory ring by default, or InfluxDB when configured. Optionally filtered to a
/// single compose project. A <see cref="MetricsRange"/> requests an explicit historical window; omit it
/// for the backend's live window. No Docker calls happen on the RPC path.
/// </summary>
[Handler("metrics.containers")]
public sealed class GetContainerMetrics(IMetricsSource source)
    : IHandler<GetContainerMetrics.Query, Result<GetContainerMetrics.Response>> {
    public sealed record Query(string? Project = null, MetricsRange? Range = null);
    public sealed record Response(IReadOnlyList<ContainerMetrics> Containers);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var readouts = await source.GetContainersAsync(query.Range.ToWindow(), ct);
        var items = readouts
            .Where(r => query.Project is null || r.Latest.StackName == query.Project)
            .Select(r => new ContainerMetrics(
                r.Latest.ContainerId,
                r.Latest.ContainerName,
                r.Latest.StackName,
                r.Latest.CpuPercent,
                r.Latest.MemUsedBytes,
                r.Latest.MemLimitBytes,
                r.Latest.MemPercent,
                r.Latest.Online,
                r.History.Select(h => new ContainerSample(h.T, h.CpuPercent, h.MemUsedBytes)).ToList()))
            .OrderByDescending(c => c.CpuPercent)
            .ThenBy(c => c.ContainerName, StringComparer.Ordinal)
            .ToList();
        return new Response(items);
    }
}
