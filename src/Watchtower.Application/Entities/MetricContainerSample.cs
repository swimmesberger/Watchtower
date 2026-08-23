namespace Watchtower.Application.Entities;

/// <summary>
/// One aggregated per-container utilization sample in the persisted metrics history (ADR-0013). Same
/// two-tier layout as <see cref="MetricHostSample"/>. Identity is the container <em>name</em>, not the
/// Docker id — names survive container recreation, which is what makes a week-long series of a
/// redeployed service one series (the InfluxDB backend keys by name for the same reason). Only online
/// containers are persisted: a stopped container contributes gaps, not zeros.
/// </summary>
public sealed class MetricContainerSample {
    public long Id { get; set; }
    /// <summary>Bucket width in seconds — 60 for minute samples, 600 for rollups.</summary>
    public int TierSeconds { get; set; }
    /// <summary>Bucket start, unix seconds (UTC).</summary>
    public long TUnixSeconds { get; set; }
    public required string ContainerName { get; set; }
    /// <summary>Compose project of the container at sample time, for the per-stack rollup.</summary>
    public string? StackName { get; set; }
    /// <summary>Average CPU% over the bucket.</summary>
    public double CpuPercent { get; set; }
    /// <summary>Average memory usage over the bucket.</summary>
    public long MemUsedBytes { get; set; }
    /// <summary>Memory limit at the end of the bucket, when the container has one.</summary>
    public long? MemLimitBytes { get; set; }
}
