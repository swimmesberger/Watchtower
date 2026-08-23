using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The write path of the database-persisted metrics history (ADR-0013). Fed by <see cref="MetricsSampler"/>
/// once per tick under the <c>database</c> backend: ticks accumulate in memory and flush as one row per series per
/// minute; a rate-limited maintenance pass rolls minute rows up into 10-minute rows and enforces the
/// two deletion windows (72h raw, <c>Metrics:RetentionDays</c> rollup). Maintenance rides the sampler
/// loop — this codebase deliberately has no separate job scheduler (sweeps ride existing loops, like
/// auth-session expiry).
///
/// <para>Singleton, called only from the sampler's single loop — the accumulators need no locking.
/// Database access goes through short-lived scopes (ADR-0004). A failed flush drops that minute and
/// keeps sampling: history gets a gap, the live ring is unaffected.</para>
/// </summary>
public sealed class MetricsPersistenceService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider clock,
    ILogger<MetricsPersistenceService> logger) {
    /// <summary>Minute tier — one row per series per minute, averaged over the sampler's 10s ticks.</summary>
    public const int RawTierSeconds = 60;

    /// <summary>Rollup tier — 10-minute averages of the minute tier, kept for the retention window.</summary>
    public const int RollupTierSeconds = 600;

    /// <summary>How long minute rows are kept. Past this, history is served from the rollup tier.</summary>
    public static readonly TimeSpan RawWindow = TimeSpan.FromHours(72);

    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);

    /// <summary>Rollup work per maintenance pass is bounded; the next pass continues where this left off.</summary>
    private const int MaxRollupBucketsPerPass = 200;

    // ── Current-minute accumulators (single-threaded: only the sampler loop touches them) ──
    private long _bucketStart = -1;
    private readonly Accumulator _host = new();
    private readonly Dictionary<string, ContainerAccumulator> _containers = new(StringComparer.Ordinal);
    private long _lastMaintenanceUnix;

    /// <summary>
    /// Accumulates one sampler tick and flushes the previous minute when the tick crossed a minute
    /// boundary. Only online containers are recorded — a stopped container is a gap, not a zero.
    /// </summary>
    public async ValueTask RecordTickAsync(
        HostSnapshot host, IReadOnlyList<ContainerSnapshot> containers, CancellationToken ct) {
        var nowUnix = clock.GetUtcNow().ToUnixTimeSeconds();
        var bucket = nowUnix / RawTierSeconds * RawTierSeconds;

        if (_bucketStart >= 0 && bucket != _bucketStart) {
            await FlushAsync(ct);
        }
        _bucketStart = bucket;

        if (host.Available) {
            _host.Add(host.CpuPercent, host.MemPercent, host.MemUsedBytes, host.LoadAvg1, host.LoadAvg5);
        }
        foreach (var c in containers) {
            if (!c.Online) continue;
            if (!_containers.TryGetValue(c.ContainerName, out var acc)) {
                acc = new ContainerAccumulator();
                _containers[c.ContainerName] = acc;
            }
            acc.Add(c);
        }

        if (nowUnix - _lastMaintenanceUnix >= (long)MaintenanceInterval.TotalSeconds) {
            _lastMaintenanceUnix = nowUnix;
            await MaintainAsync(ct);
        }
    }

    /// <summary>Writes the accumulated minute to the database and resets the accumulators.</summary>
    private async Task FlushAsync(CancellationToken ct) {
        var bucket = _bucketStart;
        var hostRow = _host.ToRow(RawTierSeconds, bucket);
        var containerRows = _containers
            .Select(kvp => kvp.Value.ToRow(RawTierSeconds, bucket, kvp.Key))
            .ToList();
        _host.Reset();
        _containers.Clear();

        if (hostRow is null && containerRows.Count == 0) return;

        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // Overwrite semantics for the (rare) restart-inside-a-minute case: the partial bucket the
            // previous process wrote is replaced rather than colliding with the unique (tier, t) index.
            // Follow-up (ADR-0024): on PostgreSQL this wants to be one INSERT ... ON CONFLICT DO UPDATE
            // per table instead of delete-then-insert. Two writers in the same minute — which only a
            // second instance produces — can currently interleave the delete and the insert; the upsert
            // makes the write atomic and halves the round trips. Left as-is here because the second
            // instance does not exist yet and this phase is about the provider, not the write paths.
            await db.MetricHostSamples
                .Where(x => x.TierSeconds == RawTierSeconds && x.TUnixSeconds == bucket)
                .ExecuteDeleteAsync(ct);
            await db.MetricContainerSamples
                .Where(x => x.TierSeconds == RawTierSeconds && x.TUnixSeconds == bucket)
                .ExecuteDeleteAsync(ct);
            if (hostRow is not null) db.MetricHostSamples.Add(hostRow);
            db.MetricContainerSamples.AddRange(containerRows);
            await db.SaveChangesAsync(ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "Metrics history flush failed; this minute becomes a gap");
        }
    }

    /// <summary>
    /// Rolls complete 10-minute buckets of minute rows up into the rollup tier, then deletes minute rows
    /// past the raw window and rollup rows past the retention window. Bounded per pass so the first run
    /// after an upgrade cannot stall a tick for long.
    /// </summary>
    public async Task MaintainAsync(CancellationToken ct) {
        var retentionDays = Math.Clamp(options.CurrentValue.Metrics.RetentionDays, 1, 365);
        var nowUnix = clock.GetUtcNow().ToUnixTimeSeconds();
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

            await RollUpAsync(db, nowUnix, ct);

            var rawCutoff = nowUnix - (long)RawWindow.TotalSeconds;
            await db.MetricHostSamples
                .Where(x => x.TierSeconds == RawTierSeconds && x.TUnixSeconds < rawCutoff)
                .ExecuteDeleteAsync(ct);
            await db.MetricContainerSamples
                .Where(x => x.TierSeconds == RawTierSeconds && x.TUnixSeconds < rawCutoff)
                .ExecuteDeleteAsync(ct);

            var retentionCutoff = nowUnix - retentionDays * 86400L;
            await db.MetricHostSamples
                .Where(x => x.TierSeconds == RollupTierSeconds && x.TUnixSeconds < retentionCutoff)
                .ExecuteDeleteAsync(ct);
            await db.MetricContainerSamples
                .Where(x => x.TierSeconds == RollupTierSeconds && x.TUnixSeconds < retentionCutoff)
                .ExecuteDeleteAsync(ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "Metrics history maintenance failed; will retry next interval");
        }
    }

    private static async Task RollUpAsync(WatchtowerDbContext db, long nowUnix, CancellationToken ct) {
        // Only buckets that can no longer gain minute rows: bucket end at or before the current minute.
        var completeBefore = nowUnix / RollupTierSeconds * RollupTierSeconds;

        // Start at the oldest minute row not already covered by a rollup. Anchoring on the raw rows
        // (not just the watermark) keeps the pass from re-scanning stretches that hold no data — a
        // watermark alone would pin the window to an old rollup and never advance across a gap.
        var hostWatermark = await db.MetricHostSamples
            .Where(x => x.TierSeconds == RollupTierSeconds)
            .MaxAsync(x => (long?)x.TUnixSeconds, ct);
        var containerWatermark = await db.MetricContainerSamples
            .Where(x => x.TierSeconds == RollupTierSeconds)
            .MaxAsync(x => (long?)x.TUnixSeconds, ct);

        var hostOldestRaw = await OldestBucketAsync(
            db.MetricHostSamples.Where(x => x.TierSeconds == RawTierSeconds
                && (hostWatermark == null || x.TUnixSeconds >= hostWatermark + RollupTierSeconds)), ct);
        var containerOldestRaw = await OldestBucketAsync(
            db.MetricContainerSamples.Where(x => x.TierSeconds == RawTierSeconds
                && (containerWatermark == null || x.TUnixSeconds >= containerWatermark + RollupTierSeconds)), ct);

        var hostFrom = hostOldestRaw;
        var containerFrom = containerOldestRaw;

        if (hostFrom is { } hf && hf < completeBefore) {
            var to = Math.Min(completeBefore, hf + (long)MaxRollupBucketsPerPass * RollupTierSeconds);
            var rollups = (await db.MetricHostSamples
                    .Where(x => x.TierSeconds == RawTierSeconds && x.TUnixSeconds >= hf && x.TUnixSeconds < to)
                    .GroupBy(x => x.TUnixSeconds / RollupTierSeconds)
                    .Select(g => new {
                        Bucket = g.Key * RollupTierSeconds,
                        Cpu = g.Average(x => x.CpuPercent),
                        Mem = g.Average(x => x.MemPercent),
                        MemUsed = g.Average(x => (double?)x.MemUsedBytes),
                        Load1 = g.Average(x => x.LoadAvg1),
                        Load5 = g.Average(x => x.LoadAvg5),
                    })
                    .ToListAsync(ct))
                .Select(r => new MetricHostSample {
                    TierSeconds = RollupTierSeconds,
                    TUnixSeconds = r.Bucket,
                    CpuPercent = r.Cpu,
                    MemPercent = r.Mem,
                    MemUsedBytes = r.MemUsed is { } mu ? (long)mu : null,
                    LoadAvg1 = r.Load1,
                    LoadAvg5 = r.Load5,
                });
            db.MetricHostSamples.AddRange(rollups);
        }

        if (containerFrom is { } cf && cf < completeBefore) {
            var to = Math.Min(completeBefore, cf + (long)MaxRollupBucketsPerPass * RollupTierSeconds);
            var rollups = (await db.MetricContainerSamples
                    .Where(x => x.TierSeconds == RawTierSeconds && x.TUnixSeconds >= cf && x.TUnixSeconds < to)
                    .GroupBy(x => new { Bucket = x.TUnixSeconds / RollupTierSeconds, x.ContainerName })
                    .Select(g => new {
                        g.Key.Bucket,
                        g.Key.ContainerName,
                        Cpu = g.Average(x => x.CpuPercent),
                        MemUsed = g.Average(x => (double)x.MemUsedBytes),
                        // Aggregate stand-ins for "latest": stable enough for a label and a limit line.
                        Stack = g.Max(x => x.StackName),
                        Limit = g.Max(x => x.MemLimitBytes),
                    })
                    .ToListAsync(ct))
                .Select(r => new MetricContainerSample {
                    TierSeconds = RollupTierSeconds,
                    TUnixSeconds = r.Bucket * RollupTierSeconds,
                    ContainerName = r.ContainerName,
                    StackName = r.Stack,
                    CpuPercent = r.Cpu,
                    MemUsedBytes = (long)r.MemUsed,
                    MemLimitBytes = r.Limit,
                });
            db.MetricContainerSamples.AddRange(rollups);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<long?> OldestBucketAsync(IQueryable<MetricHostSample> rows, CancellationToken ct) {
        var min = await rows.MinAsync(x => (long?)x.TUnixSeconds, ct);
        return min / RollupTierSeconds * RollupTierSeconds;
    }

    private static async Task<long?> OldestBucketAsync(IQueryable<MetricContainerSample> rows, CancellationToken ct) {
        var min = await rows.MinAsync(x => (long?)x.TUnixSeconds, ct);
        return min / RollupTierSeconds * RollupTierSeconds;
    }

    // ── Accumulators ─────────────────────────────────────────────────────────

    private sealed class Accumulator {
        private double _cpuSum, _memSum, _memUsedSum, _load1Sum, _load5Sum;
        private int _cpuN, _memN, _memUsedN, _load1N, _load5N;

        public void Add(double? cpu, double? mem, long? memUsed, double? load1, double? load5) {
            if (cpu is { } c) { _cpuSum += c; _cpuN++; }
            if (mem is { } m) { _memSum += m; _memN++; }
            if (memUsed is { } u) { _memUsedSum += u; _memUsedN++; }
            if (load1 is { } l1) { _load1Sum += l1; _load1N++; }
            if (load5 is { } l5) { _load5Sum += l5; _load5N++; }
        }

        public MetricHostSample? ToRow(int tier, long bucket) {
            if (_cpuN == 0 && _memN == 0 && _memUsedN == 0) return null;
            return new MetricHostSample {
                TierSeconds = tier,
                TUnixSeconds = bucket,
                CpuPercent = _cpuN > 0 ? _cpuSum / _cpuN : null,
                MemPercent = _memN > 0 ? _memSum / _memN : null,
                MemUsedBytes = _memUsedN > 0 ? (long)(_memUsedSum / _memUsedN) : null,
                LoadAvg1 = _load1N > 0 ? _load1Sum / _load1N : null,
                LoadAvg5 = _load5N > 0 ? _load5Sum / _load5N : null,
            };
        }

        public void Reset() {
            _cpuSum = _memSum = _memUsedSum = _load1Sum = _load5Sum = 0;
            _cpuN = _memN = _memUsedN = _load1N = _load5N = 0;
        }
    }

    private sealed class ContainerAccumulator {
        private double _cpuSum, _memSum;
        private int _n;
        private string? _stack;
        private long? _limit;

        public void Add(ContainerSnapshot c) {
            _cpuSum += c.CpuPercent;
            _memSum += c.MemUsedBytes;
            _n++;
            _stack = c.StackName ?? _stack;
            _limit = c.MemLimitBytes ?? _limit;
        }

        public MetricContainerSample ToRow(int tier, long bucket, string name) => new() {
            TierSeconds = tier,
            TUnixSeconds = bucket,
            ContainerName = name,
            StackName = _stack,
            CpuPercent = _n > 0 ? _cpuSum / _n : 0,
            MemUsedBytes = _n > 0 ? (long)(_memSum / _n) : 0,
            MemLimitBytes = _limit,
        };
    }
}
