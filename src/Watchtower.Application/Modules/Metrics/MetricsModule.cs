using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.Metrics.Handlers;

namespace Watchtower.Application.Modules.Metrics;

/// <summary>
/// Host and container resource metrics. All handlers read the in-memory ring buffers populated by the
/// background <c>MetricsSampler</c> — no Docker calls happen on the RPC path (amendment F5).
/// </summary>
[AppModule("Metrics")]
public static partial class MetricsModule {
    /// <summary>Returns the JSON type info resolver for Metrics module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => MetricsJsonContext.Default;
}

// ── Host ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Host CPU/RAM/load/disk snapshot. When <see cref="Available"/> is false the metric fields are null
/// and <see cref="Reason"/> is "host-proc-not-mounted"; container metrics are unaffected.
/// </summary>
public sealed record HostMetrics(
    bool Available,
    string? Reason,
    double? CpuPercent,
    int? CpuCores,
    double? LoadAvg1,
    double? LoadAvg5,
    long? MemUsedBytes,
    long? MemTotalBytes,
    double? MemPercent,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    double? DiskPercent,
    string DiskSource,
    DateTimeOffset SampledAt,
    IReadOnlyList<HostSample> History);

/// <summary>One host sparkline point (oldest→newest).</summary>
public sealed record HostSample(DateTimeOffset T, double? CpuPercent, double? MemPercent);

// ── Containers ─────────────────────────────────────────────────────────────

/// <summary>Per-container CPU/memory readout with its short sparkline history.</summary>
public sealed record ContainerMetrics(
    string ContainerId,
    string ContainerName,
    string? StackName,
    double CpuPercent,
    long MemUsedBytes,
    long? MemLimitBytes,
    double? MemPercent,
    bool Online,
    IReadOnlyList<ContainerSample> History);

/// <summary>One container sparkline point (oldest→newest).</summary>
public sealed record ContainerSample(DateTimeOffset T, double CpuPercent, long MemUsedBytes);

// ── Stacks (rollup) ─────────────────────────────────────────────────────────

/// <summary>Per-stack rollup (sum of member containers), sorted CPU-desc server-side.</summary>
public sealed record StackMetrics(
    string StackName,
    double CpuPercent,
    long MemUsedBytes,
    int ContainerCount,
    IReadOnlyList<StackSample> History);

/// <summary>One summed stack sparkline point (oldest→newest); carries both CPU and mem for the F8 toggle.</summary>
public sealed record StackSample(DateTimeOffset T, double CpuPercent, long MemUsedBytes);

/// <summary>JSON serializer context for Metrics module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(HostMetrics))]
[JsonSerializable(typeof(HostSample))]
[JsonSerializable(typeof(ContainerMetrics))]
[JsonSerializable(typeof(ContainerSample))]
[JsonSerializable(typeof(StackMetrics))]
[JsonSerializable(typeof(StackSample))]
[JsonSerializable(typeof(GetHostMetrics.Query), TypeInfoPropertyName = "GetHostMetricsQuery")]
[JsonSerializable(typeof(GetHostMetrics.Response), TypeInfoPropertyName = "GetHostMetricsResponse")]
[JsonSerializable(typeof(GetContainerMetrics.Query), TypeInfoPropertyName = "GetContainerMetricsQuery")]
[JsonSerializable(typeof(GetContainerMetrics.Response), TypeInfoPropertyName = "GetContainerMetricsResponse")]
[JsonSerializable(typeof(GetStackMetrics.Query), TypeInfoPropertyName = "GetStackMetricsQuery")]
[JsonSerializable(typeof(GetStackMetrics.Response), TypeInfoPropertyName = "GetStackMetricsResponse")]
public sealed partial class MetricsJsonContext : JsonSerializerContext;
