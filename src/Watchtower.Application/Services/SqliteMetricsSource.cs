using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The default metrics backend (ADR-0013): the live window comes straight from the in-memory ring
/// (identical to <see cref="InMemoryMetricsSource"/>), historical ranges are aggregated in SQL from the
/// rows <see cref="MetricsPersistenceService"/> persists. The tier is picked per query — minute rows
/// inside the raw window, 10-minute rollups beyond it — and either tier is bucketed server-side into
/// the requested step, mirroring the InfluxDB reader's <c>aggregateWindow</c> shape.
///
/// <para>Container identity in history is the container <em>name</em> (stable across recreation), the
/// same convention the InfluxDB backend uses; the frontend matches rows by id or name.</para>
/// </summary>
public sealed class SqliteMetricsSource(
    MetricsStore store,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<SqliteMetricsSource> logger) : IMetricsSource {
    public MetricsCapabilities Capabilities { get; } = new("sqlite", HistoryAvailable: true);

    public async ValueTask<HostReadout> GetHostAsync(MetricsWindow window, CancellationToken ct) {
        if (!window.IsHistory) {
            var (snapshot, history) = store.GetHost();
            return new HostReadout(snapshot, history);
        }

        var (tier, step, from, to) = ResolveRange(window);
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var rows = await db.MetricHostSamples
                .Where(x => x.TierSeconds == tier && x.TUnixSeconds >= from && x.TUnixSeconds <= to)
                .GroupBy(x => x.TUnixSeconds / step)
                .Select(g => new {
                    Bucket = g.Key * step,
                    Cpu = g.Average(x => x.CpuPercent),
                    Mem = g.Average(x => x.MemPercent),
                    MemUsed = g.Average(x => (double?)x.MemUsedBytes),
                })
                .OrderBy(r => r.Bucket)
                .ToListAsync(ct);

            var history = rows
                .Select(r => new HostSampleEntry(DateTimeOffset.FromUnixTimeSeconds(r.Bucket), r.Cpu, r.Mem))
                .ToList();

            // The headline numbers next to a history chart describe the host *now* — the live ring is
            // fresher and richer (cores, disk, totals) than any persisted row, so serve it alongside.
            var (live, _) = store.GetHost();
            return new HostReadout(live, history);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "SQLite metrics history host query failed");
            return new HostReadout(HostSnapshot.Unavailable("sqlite-history-error"), []);
        }
    }

    public async ValueTask<IReadOnlyList<ContainerReadout>> GetContainersAsync(MetricsWindow window, CancellationToken ct) {
        if (!window.IsHistory) return store.GetContainers();

        var (tier, step, from, to) = ResolveRange(window);
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var rows = await db.MetricContainerSamples
                .Where(x => x.TierSeconds == tier && x.TUnixSeconds >= from && x.TUnixSeconds <= to)
                .GroupBy(x => new { Bucket = x.TUnixSeconds / step, x.ContainerName })
                .Select(g => new {
                    g.Key.Bucket,
                    g.Key.ContainerName,
                    Cpu = g.Average(x => x.CpuPercent),
                    MemUsed = g.Average(x => (double)x.MemUsedBytes),
                    Stack = g.Max(x => x.StackName),
                    Limit = g.Max(x => x.MemLimitBytes),
                })
                .OrderBy(r => r.ContainerName).ThenBy(r => r.Bucket)
                .ToListAsync(ct);

            var result = new List<ContainerReadout>();
            foreach (var group in rows.GroupBy(r => r.ContainerName, StringComparer.Ordinal)) {
                var history = group
                    .Select(r => new ContainerSampleEntry(
                        DateTimeOffset.FromUnixTimeSeconds(r.Bucket * step), r.Cpu, (long)r.MemUsed))
                    .ToList();
                var last = group.Last();
                var snapshot = new ContainerSnapshot {
                    ContainerId = last.ContainerName, // name is the identity in history, as in Influx
                    ContainerName = last.ContainerName,
                    StackName = last.Stack,
                    CpuPercent = last.Cpu,
                    MemUsedBytes = (long)last.MemUsed,
                    MemLimitBytes = last.Limit,
                    MemPercent = last.Limit is > 0 ? last.MemUsed / last.Limit.Value * 100.0 : null,
                    Online = true,
                    SampledAt = DateTimeOffset.FromUnixTimeSeconds(last.Bucket * step),
                };
                result.Add(new ContainerReadout(snapshot, history));
            }
            return result;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "SQLite metrics history container query failed");
            return [];
        }
    }

    /// <summary>
    /// Picks the tier for a range (minute rows while the range start is inside the raw window, else
    /// rollups) and floors the requested step at the tier width — finer than the stored resolution
    /// would just emit one bucket per row.
    /// </summary>
    private (int Tier, long Step, long From, long To) ResolveRange(MetricsWindow window) {
        var nowUnix = clock.GetUtcNow().ToUnixTimeSeconds();
        var from = window.From!.Value.ToUnixTimeSeconds();
        var to = window.To!.Value.ToUnixTimeSeconds();
        var tier = from >= nowUnix - (long)MetricsPersistenceService.RawWindow.TotalSeconds
            ? MetricsPersistenceService.RawTierSeconds
            : MetricsPersistenceService.RollupTierSeconds;
        var step = Math.Max((long)(window.Step ?? TimeSpan.FromMinutes(1)).TotalSeconds, tier);
        return (tier, step, from, to);
    }
}
