using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Returns the latest host CPU/RAM/load/disk snapshot plus its sparkline history. Reads only the
/// in-memory <see cref="MetricsStore"/> (no Docker calls — the background sampler is the sole writer).
/// </summary>
[Handler("metrics.host")]
public sealed class GetHostMetrics(MetricsStore store)
    : IHandler<GetHostMetrics.Query, Result<GetHostMetrics.Response>> {
    public sealed record Query;
    public sealed record Response(HostMetrics Host);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var (snap, history) = store.GetHost();
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
        return ValueTask.FromResult<Result<Response>>(new Response(host));
    }
}
