namespace Watchtower.Application.Services;

/// <summary>
/// The default metrics backend (ADR-0007): serves the in-memory ring buffers that
/// <see cref="MetricsSampler"/> fills. Zero external dependency. It has only the live window — a history
/// request returns whatever the ~15-minute ring currently holds, and <see cref="Capabilities"/> reports
/// <c>HistoryAvailable = false</c> so the UI never offers a time-range picker against it.
/// </summary>
public sealed class InMemoryMetricsSource(MetricsStore store) : IMetricsSource {
    public MetricsCapabilities Capabilities { get; } = new("memory", HistoryAvailable: false);

    public ValueTask<HostReadout> GetHostAsync(MetricsWindow window, CancellationToken ct) {
        var (snapshot, history) = store.GetHost();
        return ValueTask.FromResult(new HostReadout(snapshot, history));
    }

    public ValueTask<IReadOnlyList<ContainerReadout>> GetContainersAsync(MetricsWindow window, CancellationToken ct) =>
        ValueTask.FromResult(store.GetContainers());
}
