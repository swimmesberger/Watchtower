using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Metrics.Handlers;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics;

/// <summary>
/// Host and container resource metrics. All handlers read the active <c>IMetricsSource</c> backend
/// (ADR-0007, amended by ADR-0013) — no Docker calls happen on the RPC path (amendment F5).
/// </summary>
/// <remarks>
/// Exposes the <c>metrics-history</c> client flag (ADR-0030): true when the active metrics backend can
/// answer historical time ranges (the database and influxdb backends). Resolved per session fetch by
/// <c>WatchtowerFeatureFlagService</c> from <c>IMetricsSource.Capabilities</c>, so it follows a runtime
/// backend switch (<c>metrics.updateConfig</c>); the frontend gates the History view on it.
/// </remarks>
[AppModule("Metrics")]
[ClientFeatures("metrics-history")]
public static partial class MetricsModule {
    /// <summary>Returns the JSON type info resolver for Metrics module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => MetricsJsonContext.Default;
}

// ── Backend configuration (ADR-0013) ─────────────────────────────────────────

/// <summary>
/// The effective metrics-backend configuration surfaced to the Settings page. The InfluxDB token never
/// leaves the server — <see cref="MetricsInfluxConfig.HasToken"/> stands in for it.
/// <see cref="PinnedPaths"/> lists the configuration paths pinned by <c>WATCHTOWER__*</c> env vars
/// (env wins over the settings store); the UI disables those fields.
/// </summary>
public sealed record MetricsConfig(
    string Backend,
    int RetentionDays,
    bool HistoryAvailable,
    MetricsInfluxConfig Influx,
    string[] PinnedPaths);

/// <summary>InfluxDB connection values for the config surface (token reduced to a flag).</summary>
public sealed record MetricsInfluxConfig(
    string? Url,
    string? Org,
    string? Bucket,
    bool HasToken,
    string ComposeProjectTag,
    string DiskMountpoint);

/// <summary>Maps the resolved backend to its canonical config-value spelling.</summary>
internal static class MetricsBackendKindExtensions {
    public static string ToConfigValue(this MetricsBackendKind kind) => kind switch {
        MetricsBackendKind.Memory => "memory",
        MetricsBackendKind.Influxdb => "influxdb",
        _ => "database",
    };
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
[JsonSerializable(typeof(MetricsConfig))]
[JsonSerializable(typeof(MetricsInfluxConfig))]
[JsonSerializable(typeof(GetMetricsConfig.Query), TypeInfoPropertyName = "GetMetricsConfigQuery")]
[JsonSerializable(typeof(GetMetricsConfig.Response), TypeInfoPropertyName = "GetMetricsConfigResponse")]
[JsonSerializable(typeof(UpdateMetricsConfig.Command), TypeInfoPropertyName = "UpdateMetricsConfigCommand")]
[JsonSerializable(typeof(UpdateMetricsConfig.Response), TypeInfoPropertyName = "UpdateMetricsConfigResponse")]
public sealed partial class MetricsJsonContext : JsonSerializerContext;
