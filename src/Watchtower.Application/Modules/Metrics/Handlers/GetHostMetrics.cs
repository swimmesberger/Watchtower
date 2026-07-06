using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Returns the latest host CPU/RAM/load/disk snapshot plus its history. Reads only the active
/// <see cref="IMetricsSource"/> (ADR-0007) — the in-memory ring by default, or InfluxDB when configured;
/// no Docker calls happen on the RPC path. A <see cref="MetricsRange"/> requests an explicit historical
/// window; omit it for the backend's live window.
/// </summary>
[Handler("metrics.host")]
public sealed class GetHostMetrics(IMetricsSource source)
    : IHandler<GetHostMetrics.Query, Result<GetHostMetrics.Response>> {
    public sealed record Query(MetricsRange? Range = null);
    public sealed record Response(HostMetrics Host);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var (snap, history) = await source.GetHostAsync(query.Range.ToWindow(), ct);
        var host = new HostMetrics(
            snap.Available,
            snap.Reason,
            snap.CpuPercent,
            snap.CpuCores,
            snap.LoadAvg1,
            snap.LoadAvg5,
            snap.MemUsedBytes,
            snap.MemTotalBytes,
            snap.MemPercent,
            snap.DiskUsedBytes,
            snap.DiskTotalBytes,
            snap.DiskPercent,
            snap.DiskSource,
            snap.SampledAt,
            history.Select(h => new HostSample(h.T, h.CpuPercent, h.MemPercent)).ToList());
        return new Response(host);
    }
}
