using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Returns per-container CPU/memory readouts (with sparkline history) from the in-memory
/// <see cref="MetricsStore"/>. Optionally filtered to a single compose project. No Docker calls.
/// </summary>
[Handler("metrics.containers")]
public sealed class GetContainerMetrics(MetricsStore store)
    : IHandler<GetContainerMetrics.Query, Result<GetContainerMetrics.Response>> {
    public sealed record Query(string? Project = null);
    public sealed record Response(IReadOnlyList<ContainerMetrics> Containers);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var readouts = store.GetContainers();
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
        return ValueTask.FromResult<Result<Response>>(new Response(items));
    }
}
