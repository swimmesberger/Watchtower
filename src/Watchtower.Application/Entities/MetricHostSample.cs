namespace Watchtower.Application.Entities;

/// <summary>
/// One aggregated host utilization sample in the SQLite metrics history (ADR-0013). Rows exist in two
/// tiers, discriminated by <see cref="TierSeconds"/>: minute averages of the sampler's 10s ticks
/// (60, kept for the raw window) and coarser rollups of those minutes (600, kept for the retention
/// window). <see cref="TUnixSeconds"/> is the bucket start; integer seconds rather than a DateTimeOffset
/// column so range scans and bucket grouping stay integer arithmetic in SQLite.
/// </summary>
public sealed class MetricHostSample {
    public long Id { get; set; }
    /// <summary>Bucket width in seconds — 60 for minute samples, 600 for rollups.</summary>
    public int TierSeconds { get; set; }
    /// <summary>Bucket start, unix seconds (UTC).</summary>
    public long TUnixSeconds { get; set; }
    /// <summary>Average CPU%, null when the host /proc mount produced no reading in the bucket.</summary>
    public double? CpuPercent { get; set; }
    /// <summary>Average RAM%, null when unavailable.</summary>
    public double? MemPercent { get; set; }
    public long? MemUsedBytes { get; set; }
    public double? LoadAvg1 { get; set; }
    public double? LoadAvg5 { get; set; }
}
