using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.Metrics.Handlers;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics;

/// <summary>
/// Host and container resource metrics. All handlers read the active <c>IMetricsSource</c> backend
/// (ADR-0007) — no Docker calls happen on the RPC path (amendment F5).
/// </summary>
/// <remarks>
/// Exposes the <c>metrics-history</c> client flag (ADR-0030): true when the active metrics backend can
/// answer historical time ranges (the InfluxDB backend). Resolved by <c>MetricsFeatureFlagService</c> from
/// <c>IMetricsSource.Capabilities</c> and surfaced to the frontend via the <c>elarion.session</c> snapshot,
/// which gates the History view.
/// </remarks>
[AppModule("Metrics")]
[ClientFeatures("metrics-history")]
public static partial class MetricsModule {
    /// <summary>Returns the JSON type info resolver for Metrics module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => MetricsJsonContext.Default;
}

// ── Range + capabilities ─────────────────────────────────────────────────────

/// <summary>
/// An explicit historical range on a metrics query (ADR-0007). Omitted (null) ⇒ the backend's live
/// window. <paramref name="StepSeconds"/> is the server-side downsample bucket that bounds the returned
/// point count. Only honored when the active backend reports <c>historyAvailable</c>.
/// </summary>
public sealed record MetricsRange(DateTimeOffset From, DateTimeOffset To, int StepSeconds);

/// <summary>Maps the RPC-facing <see cref="MetricsRange"/> to the service-layer <see cref="MetricsWindow"/>.</summary>
internal static class MetricsRangeExtensions {
    public static MetricsWindow ToWindow(this MetricsRange? range) =>
        range is null
            ? MetricsWindow.Live
            : MetricsWindow.History(range.From, range.To, TimeSpan.FromSeconds(Math.Max(1, range.StepSeconds)));
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
[JsonSerializable(typeof(MetricsRange))]
[JsonSerializable(typeof(GetHostMetrics.Query), TypeInfoPropertyName = "GetHostMetricsQuery")]
[JsonSerializable(typeof(GetHostMetrics.Response), TypeInfoPropertyName = "GetHostMetricsResponse")]
[JsonSerializable(typeof(GetContainerMetrics.Query), TypeInfoPropertyName = "GetContainerMetricsQuery")]
[JsonSerializable(typeof(GetContainerMetrics.Response), TypeInfoPropertyName = "GetContainerMetricsResponse")]
[JsonSerializable(typeof(GetStackMetrics.Query), TypeInfoPropertyName = "GetStackMetricsQuery")]
[JsonSerializable(typeof(GetStackMetrics.Response), TypeInfoPropertyName = "GetStackMetricsResponse")]
public sealed partial class MetricsJsonContext : JsonSerializerContext;
