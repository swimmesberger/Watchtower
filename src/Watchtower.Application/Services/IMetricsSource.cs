namespace Watchtower.Application.Services;

/// <summary>
/// The read abstraction every <c>metrics.*</c> handler depends on (ADR-0007, amended by ADR-0013).
/// Backends: the SQLite-persisted default (<see cref="SqliteMetricsSource"/>), the live-only ring
/// (<see cref="InMemoryMetricsSource"/>), and the BYO InfluxDB reader (<see cref="InfluxMetricsSource"/>).
/// The registered implementation is <see cref="MetricsSourceRouter"/>, which delegates to whichever
/// backend the current options select — runtime-switchable, while the sampler's per-tick backend check
/// keeps a single active collector. The RPC path never fans out to Docker.
/// </summary>
public interface IMetricsSource {
    /// <summary>Static description of what this backend can do — source name and history availability.</summary>
    MetricsCapabilities Capabilities { get; }

    /// <summary>The latest host snapshot bundled with its sample history over <paramref name="window"/>.</summary>
    ValueTask<HostReadout> GetHostAsync(MetricsWindow window, CancellationToken ct);

    /// <summary>Per-container readouts (latest + history) over <paramref name="window"/>.</summary>
    ValueTask<IReadOnlyList<ContainerReadout>> GetContainersAsync(MetricsWindow window, CancellationToken ct);
}

/// <summary>Host snapshot bundled with its sample history — the host analogue of <see cref="ContainerReadout"/>.</summary>
public sealed record HostReadout(HostSnapshot Snapshot, IReadOnlyList<HostSampleEntry> History);

/// <summary>
/// A read window. The default (<see cref="Live"/>) means "the backend's live window" — the in-memory
/// ring's ~15 minutes, or a recent slice from InfluxDB. <see cref="History"/> names an explicit
/// <c>[From,To]</c> range plus the downsample <see cref="Step"/> used to bound the returned point count.
/// </summary>
public readonly record struct MetricsWindow {
    /// <summary>Inclusive start of a historical range; null for the live window.</summary>
    public DateTimeOffset? From { get; private init; }

    /// <summary>Inclusive end of a historical range; null for the live window.</summary>
    public DateTimeOffset? To { get; private init; }

    /// <summary>Downsample bucket size for a historical range (server-side aggregation).</summary>
    public TimeSpan? Step { get; private init; }

    /// <summary>True when this is an explicit historical range rather than the backend's live window.</summary>
    public bool IsHistory => From is not null && To is not null;

    /// <summary>The backend's default live window (recent slice).</summary>
    public static MetricsWindow Live => default;

    /// <summary>An explicit downsampled historical range. <paramref name="step"/> is floored at 1s.</summary>
    public static MetricsWindow History(DateTimeOffset from, DateTimeOffset to, TimeSpan step) => new() {
        From = from,
        To = to,
        Step = step >= TimeSpan.FromSeconds(1) ? step : TimeSpan.FromSeconds(1),
    };
}

/// <summary>
/// Static capabilities of the active metrics backend, surfaced to the UI so it can gate the time-range
/// view and label the data source (ADR-0007).
/// </summary>
/// <param name="Source">Backend id: <c>memory</c>, <c>sqlite</c>, or <c>influxdb</c>.</param>
/// <param name="HistoryAvailable">True when the backend can answer explicit historical ranges.</param>
public sealed record MetricsCapabilities(string Source, bool HistoryAvailable);
