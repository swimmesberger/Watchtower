using Elarion.Abstractions.Features;

namespace Watchtower.Application.Services;

/// <summary>
/// Resolves Watchtower's client-exposed feature flags (ADR-0030) from deployment composition rather than a
/// flag store. <c>metrics-history</c> — declared via <c>[ClientFeatures]</c> on the Metrics module — is true
/// exactly when the active <see cref="IMetricsSource"/> backend can answer historical time ranges (the
/// InfluxDB backend, ADR-0007). Boot-fixed by DI, so the deployment-scoped session snapshot is the right
/// carrier; runtime-variable availability (e.g. Docker unreachable) stays on the data as
/// <c>available</c>/<c>reason</c>. Unknown names fail closed.
/// </summary>
public sealed class MetricsFeatureFlagService(IMetricsSource metrics) : IFeatureFlagService {
    /// <summary>The flag name exposed on the Metrics module's <c>[ClientFeatures]</c>.</summary>
    public const string HistoryFlag = "metrics-history";

    public ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default) =>
        ValueTask.FromResult(
            string.Equals(feature, HistoryFlag, StringComparison.Ordinal)
            && metrics.Capabilities.HistoryAvailable);
}
