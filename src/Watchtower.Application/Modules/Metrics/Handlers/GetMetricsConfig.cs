using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Returns the effective metrics-backend configuration (ADR-0013) for the Settings page: the resolved
/// backend, the sqlite retention window, and the InfluxDB connection values — with the token reduced to
/// a has-a-value flag, because it is a secret and the UI only needs to know whether one is stored.
/// </summary>
[Handler("metrics.getConfig")]
public sealed class GetMetricsConfig(IOptionsMonitor<WatchtowerOptions> options, IMetricsSource source)
    : IHandler<GetMetricsConfig.Query, Result<GetMetricsConfig.Response>> {
    public sealed record Query;
    public sealed record Response(MetricsConfig Config);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var metrics = options.CurrentValue.Metrics;
        var config = new MetricsConfig(
            Backend: metrics.ResolveBackend().ToConfigValue(),
            RetentionDays: Math.Clamp(metrics.RetentionDays, 1, 365),
            HistoryAvailable: source.Capabilities.HistoryAvailable,
            Influx: new MetricsInfluxConfig(
                Url: metrics.Influx.Url,
                Org: metrics.Influx.Org,
                Bucket: metrics.Influx.Bucket,
                HasToken: !string.IsNullOrWhiteSpace(metrics.Influx.Token),
                ComposeProjectTag: metrics.Influx.ComposeProjectTag,
                DiskMountpoint: metrics.Influx.DiskMountpoint));
        return ValueTask.FromResult<Result<Response>>(new Response(config));
    }
}
