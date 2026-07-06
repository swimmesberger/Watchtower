using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Reports the active metrics backend's capabilities (ADR-0007) so the frontend can gate the time-range
/// history view and label the data source. <c>source</c> is <c>memory</c> or <c>influxdb</c>;
/// <c>historyAvailable</c> is true only when the backend can answer explicit historical ranges.
/// </summary>
[Handler("metrics.capabilities")]
public sealed class GetMetricsCapabilities(IMetricsSource source)
    : IHandler<GetMetricsCapabilities.Query, Result<GetMetricsCapabilities.Response>> {
    public sealed record Query;
    public sealed record Response(string Source, bool HistoryAvailable);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) =>
        ValueTask.FromResult<Result<Response>>(
            new Response(source.Capabilities.Source, source.Capabilities.HistoryAvailable));
}
