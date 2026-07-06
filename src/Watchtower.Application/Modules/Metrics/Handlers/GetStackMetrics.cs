using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Pre-aggregated per-stack rollup for the Dashboard "who eats the resources" ranking. Sums member
/// container metrics by compose project, sorted CPU-desc server-side, so the Dashboard renders one
/// already-ordered query. Reads only the active <see cref="IMetricsSource"/> (ADR-0007) — no Docker
/// calls. Non-compose (stackless) containers are excluded — the ranking is per-stack. A
/// <see cref="MetricsRange"/> requests an explicit historical window; omit it for the live window.
/// </summary>
[Handler("metrics.stacks")]
public sealed class GetStackMetrics(IMetricsSource source)
    : IHandler<GetStackMetrics.Query, Result<GetStackMetrics.Response>> {
    public sealed record Query(MetricsRange? Range = null);
    public sealed record Response(IReadOnlyList<StackMetrics> Stacks, DateTimeOffset SampledAt);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var readouts = await source.GetContainersAsync(query.Range.ToWindow(), ct);

        var stacks = readouts
            .Where(r => r.Latest.StackName is not null && r.Latest.Online)
            .GroupBy(r => r.Latest.StackName!)
            .Select(g => new StackMetrics(
                g.Key,
                g.Sum(r => r.Latest.CpuPercent),
                g.Sum(r => r.Latest.MemUsedBytes),
                g.Count(),
                SumHistory(g)))
            .OrderByDescending(s => s.CpuPercent)
            .ThenBy(s => s.StackName, StringComparer.Ordinal)
            .ToList();

        return new Response(stacks, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Sums the member containers' history rings into one stack ring, bucketed by sample timestamp
    /// (rings share the sampler tick cadence, but individual containers may have shorter histories).
    /// Emits oldest→newest.
    /// </summary>
    private static IReadOnlyList<StackSample> SumHistory(IEnumerable<ContainerReadout> members) {
        var byTick = new SortedDictionary<long, (double Cpu, long Mem)>();
        foreach (var member in members) {
            foreach (var sample in member.History) {
                // Bucket to whole seconds so ticks recorded microseconds apart still align.
                var key = sample.T.ToUnixTimeSeconds();
                var current = byTick.TryGetValue(key, out var v) ? v : (0d, 0L);
                byTick[key] = (current.Item1 + sample.CpuPercent, current.Item2 + sample.MemUsedBytes);
            }
        }
        return byTick
            .Select(kvp => new StackSample(
                DateTimeOffset.FromUnixTimeSeconds(kvp.Key), kvp.Value.Cpu, kvp.Value.Mem))
            .ToList();
    }
}
