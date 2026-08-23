using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// The registered <see cref="IMetricsSource"/> (ADR-0013): delegates every call to the backend the
/// <em>current</em> options select, which makes the backend runtime-switchable — persisting
/// <c>Watchtower:Metrics:*</c> through the settings store re-binds the options and the very next read
/// (including the <c>metrics-history</c> flag evaluation) lands on the new backend.
///
/// <para>The memory and database sources are cheap always-on singletons. The InfluxDB reader is built
/// lazily from the options snapshot in use and rebuilt when the connection settings change; connection
/// settings that fail its constructor's validation degrade to an "unavailable" stand-in (reason
/// <c>influx-misconfigured</c>) instead of faulting resolution — under runtime switching a bad config
/// must be recoverable from the same UI that caused it.</para>
/// </summary>
public sealed class MetricsSourceRouter(
    InMemoryMetricsSource memory,
    DatabaseMetricsSource database,
    IOptionsMonitor<WatchtowerOptions> options,
    ILoggerFactory loggerFactory) : IMetricsSource, IDisposable {
    private readonly object _lock = new();
    private InfluxMetricsSource? _influx;
    private InfluxOptions? _influxBuiltFrom;
    private bool _influxFailedLogged;

    /// <summary>The backend currently selected by configuration.</summary>
    public MetricsBackendKind ActiveBackend => options.CurrentValue.Metrics.ResolveBackend();

    public MetricsCapabilities Capabilities => Current.Capabilities;

    public ValueTask<HostReadout> GetHostAsync(MetricsWindow window, CancellationToken ct) =>
        Current.GetHostAsync(window, ct);

    public ValueTask<IReadOnlyList<ContainerReadout>> GetContainersAsync(MetricsWindow window, CancellationToken ct) =>
        Current.GetContainersAsync(window, ct);

    private IMetricsSource Current => options.CurrentValue.Metrics.ResolveBackend() switch {
        MetricsBackendKind.Memory => memory,
        MetricsBackendKind.Influxdb => GetOrCreateInflux(),
        _ => database,
    };

    /// <summary>
    /// Returns the InfluxDB reader for the current connection settings, rebuilding it when they change.
    /// A constructor failure (missing url/org/bucket/token) yields the unavailable stand-in until the
    /// settings are fixed.
    /// </summary>
    private IMetricsSource GetOrCreateInflux() {
        var current = options.CurrentValue;
        lock (_lock) {
            if (_influx is not null && Equals(_influxBuiltFrom, current.Metrics.Influx)) return _influx;

            _influx?.Dispose();
            _influx = null;
            _influxBuiltFrom = current.Metrics.Influx;
            try {
                _influx = new InfluxMetricsSource(
                    Microsoft.Extensions.Options.Options.Create(current),
                    loggerFactory.CreateLogger<InfluxMetricsSource>());
                _influxFailedLogged = false;
                return _influx;
            } catch (InvalidOperationException ex) {
                if (!_influxFailedLogged) {
                    _influxFailedLogged = true;
                    loggerFactory.CreateLogger<MetricsSourceRouter>().LogWarning(
                        "InfluxDB metrics backend selected but not usable: {Reason}", ex.Message);
                }
                return InfluxUnavailable.Instance;
            }
        }
    }

    public void Dispose() {
        lock (_lock) {
            _influx?.Dispose();
            _influx = null;
        }
    }

    /// <summary>Stand-in served while the InfluxDB backend is selected but misconfigured.</summary>
    private sealed class InfluxUnavailable : IMetricsSource {
        public static readonly InfluxUnavailable Instance = new();

        public MetricsCapabilities Capabilities { get; } = new("influxdb", HistoryAvailable: true);

        public ValueTask<HostReadout> GetHostAsync(MetricsWindow window, CancellationToken ct) =>
            ValueTask.FromResult(new HostReadout(HostSnapshot.Unavailable("influx-misconfigured"), []));

        public ValueTask<IReadOnlyList<ContainerReadout>> GetContainersAsync(MetricsWindow window, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ContainerReadout>>([]);
    }
}
