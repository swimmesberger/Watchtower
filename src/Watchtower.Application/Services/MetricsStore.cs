using System.Collections.Concurrent;

namespace Watchtower.Application.Services;

/// <summary>
/// Thread-safe in-memory ring buffers holding a short history (~15 min @ 10s ≈ 90 samples) of
/// host and per-container resource samples. Populated exclusively by <see cref="MetricsSampler"/>
/// and read (snapshotted) by the <c>metrics.*</c> RPC handlers — no Docker calls happen on the
/// RPC path (amendment F5).
///
/// <para>
/// Registered as a singleton. All public members are safe to call concurrently: the host ring is
/// guarded by a lock, and the per-container map is a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// whose per-container rings are individually locked.
/// </para>
/// </summary>
public sealed class MetricsStore {
    /// <summary>Ring capacity — ~15 minutes of history at the 10s sample cadence.</summary>
    public const int Capacity = 90;

    private readonly object _hostLock = new();
    private readonly Queue<HostSampleEntry> _hostRing = new(Capacity);
    private HostSnapshot _host = HostSnapshot.Unavailable("host-proc-not-mounted");

    private readonly ConcurrentDictionary<string, ContainerRing> _containers = new();

    // ── Host ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the latest host sample and appends its sparkline point to the host ring.
    /// Passing an unavailable snapshot (host /proc not mounted) still updates the current
    /// snapshot but appends no history point.
    /// </summary>
    public void RecordHost(HostSnapshot snapshot) {
        lock (_hostLock) {
            _host = snapshot;
            if (snapshot.Available) {
                if (_hostRing.Count >= Capacity) _hostRing.Dequeue();
                _hostRing.Enqueue(new HostSampleEntry(snapshot.SampledAt, snapshot.CpuPercent, snapshot.MemPercent));
            }
        }
    }

    /// <summary>Returns the current host snapshot together with an oldest→newest history copy.</summary>
    public (HostSnapshot Snapshot, IReadOnlyList<HostSampleEntry> History) GetHost() {
        lock (_hostLock) {
            return (_host, _hostRing.ToArray());
        }
    }

    // ── Containers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a per-container sample, creating the container's ring on first sight and appending
    /// a sparkline point. Meta (name / stack / online / mem limit) is refreshed on every call.
    /// </summary>
    public void RecordContainer(ContainerSnapshot snapshot) {
        var ring = _containers.GetOrAdd(snapshot.ContainerId, _ => new ContainerRing());
        ring.Add(snapshot);
    }

    /// <summary>
    /// Removes rings for containers that are no longer present (called by the sampler each tick with
    /// the set of live container IDs) so stopped/removed containers don't linger forever.
    /// </summary>
    public void PruneContainersExcept(IReadOnlySet<string> liveContainerIds) {
        foreach (var id in _containers.Keys) {
            if (!liveContainerIds.Contains(id))
                _containers.TryRemove(id, out _);
        }
    }

    /// <summary>Snapshots all known containers, each with an oldest→newest history copy.</summary>
    public IReadOnlyList<ContainerReadout> GetContainers() {
        var result = new List<ContainerReadout>(_containers.Count);
        foreach (var ring in _containers.Values) {
            var readout = ring.Read();
            if (readout is not null) result.Add(readout);
        }
        return result;
    }

    /// <summary>Per-container ring buffer; each instance is individually locked.</summary>
    private sealed class ContainerRing {
        private readonly object _lock = new();
        private readonly Queue<ContainerSampleEntry> _ring = new(Capacity);
        private ContainerSnapshot? _latest;

        public void Add(ContainerSnapshot snapshot) {
            lock (_lock) {
                _latest = snapshot;
                if (_ring.Count >= Capacity) _ring.Dequeue();
                _ring.Enqueue(new ContainerSampleEntry(
                    snapshot.SampledAt, snapshot.CpuPercent, snapshot.MemUsedBytes));
            }
        }

        public ContainerReadout? Read() {
            lock (_lock) {
                if (_latest is null) return null;
                return new ContainerReadout(_latest, _ring.ToArray());
            }
        }
    }
}

/// <summary>One point in the host sparkline ring.</summary>
public sealed record HostSampleEntry(DateTimeOffset T, double? CpuPercent, double? MemPercent);

/// <summary>One point in a container sparkline ring.</summary>
public sealed record ContainerSampleEntry(DateTimeOffset T, double CpuPercent, long MemUsedBytes);

/// <summary>
/// The latest host resource sample. When <see cref="Available"/> is false the metric fields are
/// null and <see cref="Reason"/> explains why (e.g. host /proc not mounted).
/// </summary>
public sealed record HostSnapshot {
    public required bool Available { get; init; }
    public string? Reason { get; init; }
    public double? CpuPercent { get; init; }
    public int? CpuCores { get; init; }
    public double? LoadAvg1 { get; init; }
    public double? LoadAvg5 { get; init; }
    public long? MemUsedBytes { get; init; }
    public long? MemTotalBytes { get; init; }
    public double? MemPercent { get; init; }
    public long? DiskUsedBytes { get; init; }
    public long? DiskTotalBytes { get; init; }
    public double? DiskPercent { get; init; }
    /// <summary>"host-rootfs" | "docker-df" | "unavailable".</summary>
    public required string DiskSource { get; init; }
    public required DateTimeOffset SampledAt { get; init; }

    /// <summary>Builds an unavailable host snapshot (all metrics null) with the given reason.</summary>
    public static HostSnapshot Unavailable(string reason) => new() {
        Available = false,
        Reason = reason,
        DiskSource = "unavailable",
        SampledAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>The latest per-container resource sample recorded by the sampler.</summary>
public sealed record ContainerSnapshot {
    public required string ContainerId { get; init; }
    public required string ContainerName { get; init; }
    public string? StackName { get; init; }
    public required double CpuPercent { get; init; }
    public required long MemUsedBytes { get; init; }
    public long? MemLimitBytes { get; init; }
    public double? MemPercent { get; init; }
    public required bool Online { get; init; }
    public required DateTimeOffset SampledAt { get; init; }
}

/// <summary>A container's latest snapshot bundled with its oldest→newest history.</summary>
public sealed record ContainerReadout(ContainerSnapshot Latest, IReadOnlyList<ContainerSampleEntry> History);
