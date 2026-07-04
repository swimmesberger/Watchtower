using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// Single background sampler (amendment F5) that every ~10s snapshots host resource usage (from the
/// host <c>/proc</c> mount, if configured — amendment F7) and per-container CPU/memory stats, writing
/// them into the singleton <see cref="MetricsStore"/>. All <c>metrics.*</c> RPC handlers read only the
/// store, so no Docker fan-out happens on the request path.
///
/// <para>Resilience: one failing container's stats never abort a tick; a Docker-unreachable engine
/// marks the host sample errored/unavailable and the loop keeps ticking.</para>
/// </summary>
public sealed class MetricsSampler(
    DockerEngineClient docker,
    MetricsStore store,
    ILogger<MetricsSampler> logger) : BackgroundService {
    private const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>Sample cadence. The store keeps ~90 samples ⇒ ~15 min of history.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

    /// <summary>Give the engine a moment to settle before the first sample.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>Max concurrent per-container stats calls, so a big fleet doesn't hammer the socket.</summary>
    private const int MaxStatsConcurrency = 8;

    /// <summary><c>/system/df</c> is expensive — only refresh the disk fallback this often.</summary>
    private static readonly TimeSpan DiskFallbackInterval = TimeSpan.FromMinutes(5);

    // Host env vars (amendment F7) — read once; a restart is required to change mounts anyway.
    private readonly string? _hostProc = Environment.GetEnvironmentVariable("WATCHTOWER_HOST_PROC");
    private readonly string? _hostRootfs = Environment.GetEnvironmentVariable("WATCHTOWER_HOST_ROOTFS");

    // Host CPU delta state (needs two consecutive /proc/stat reads).
    private CpuTimes? _prevCpuTimes;

    // Per-container CPU delta state — skip a container's first tick (no precpu baseline on cold engines).
    private readonly Dictionary<string, bool> _containerSeen = new();

    // Lazy disk fallback (docker df) cache.
    private DateTimeOffset _lastDiskFallback = DateTimeOffset.MinValue;
    private (long Used, long Total)? _diskFallback;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(InitialDelay, stoppingToken);
        } catch (OperationCanceledException) {
            return;
        }

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await SampleAsync(stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                // Docker unreachable or an unexpected fault — mark host unavailable and keep ticking.
                logger.LogWarning(ex, "Metrics sample tick failed; will retry in {Interval}", SampleInterval);
            }

            try {
                await Task.Delay(SampleInterval, stoppingToken);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task SampleAsync(CancellationToken ct) {
        // ── Containers first (works with no host mounts) ─────────────────────────
        IReadOnlyList<DockerContainerInfo> containers;
        try {
            containers = await docker.ListAllContainersAsync(ct);
        } catch (HttpRequestException ex) {
            // Docker unreachable — record an unavailable host sample and bail this tick.
            logger.LogWarning(ex, "Docker engine unreachable during metrics sample");
            store.RecordHost(HostSnapshot.Unavailable("docker-unreachable"));
            return;
        }

        var liveIds = new HashSet<string>(containers.Count);
        var running = new List<DockerContainerInfo>(containers.Count);
        foreach (var c in containers) {
            liveIds.Add(c.Id);
            if (string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
                running.Add(c);
            else
                RecordOfflineContainer(c);
        }

        // Prune rings for containers that disappeared, and forget their CPU-delta baseline.
        store.PruneContainersExcept(liveIds);
        foreach (var goneId in _containerSeen.Keys.Where(id => !liveIds.Contains(id)).ToList())
            _containerSeen.Remove(goneId);

        await SampleRunningContainersAsync(running, ct);

        // ── Host sample ──────────────────────────────────────────────────────────
        var host = await BuildHostSnapshotAsync(ct);
        store.RecordHost(host);
    }

    private void RecordOfflineContainer(DockerContainerInfo c) {
        store.RecordContainer(new ContainerSnapshot {
            ContainerId = c.Id,
            ContainerName = ContainerName(c),
            StackName = c.Labels.TryGetValue(ComposeProjectLabel, out var p) ? p : null,
            CpuPercent = 0,
            MemUsedBytes = 0,
            MemLimitBytes = null,
            MemPercent = null,
            Online = false,
            SampledAt = DateTimeOffset.UtcNow,
        });
    }

    private async Task SampleRunningContainersAsync(IReadOnlyList<DockerContainerInfo> running, CancellationToken ct) {
        using var gate = new SemaphoreSlim(MaxStatsConcurrency);
        var tasks = running.Select(async c => {
            await gate.WaitAsync(ct);
            try {
                var stats = await docker.GetContainerStatsAsync(c.Id, ct);
                return (Container: c, Stats: stats, Ok: true);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // One failing container never kills the loop — record it offline and move on.
                logger.LogDebug(ex, "Stats read failed for container {Id}", c.Id);
                return (Container: c, Stats: (DockerContainerStats?)null, Ok: false);
            } finally {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var (container, stats, ok) in results) {
            if (!ok || stats is null) {
                RecordOfflineContainer(container);
                _containerSeen.Remove(container.Id);
                continue;
            }
            RecordContainerStats(container, stats);
        }
    }

    private void RecordContainerStats(DockerContainerInfo c, DockerContainerStats stats) {
        var firstSample = !_containerSeen.TryGetValue(c.Id, out var seen) || !seen;
        _containerSeen[c.Id] = true;

        var cpuPercent = ComputeCpuPercent(stats);
        // On the very first tick there is no usable precpu baseline on a cold engine — report 0.
        if (firstSample) cpuPercent = 0;

        var (memUsed, memLimit, memPercent) = ComputeMemory(stats);

        store.RecordContainer(new ContainerSnapshot {
            ContainerId = c.Id,
            ContainerName = ContainerName(c),
            StackName = c.Labels.TryGetValue(ComposeProjectLabel, out var p) ? p : null,
            CpuPercent = cpuPercent,
            MemUsedBytes = memUsed,
            MemLimitBytes = memLimit,
            MemPercent = memPercent,
            Online = true,
            SampledAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Standard Docker CPU% formula: <c>(cpuDelta / systemDelta) * onlineCpus * 100</c>, guarded
    /// against zero/missing deltas (returns 0 rather than NaN/∞).
    /// </summary>
    private static double ComputeCpuPercent(DockerContainerStats stats) {
        var cpu = stats.CpuStats;
        var pre = stats.PreCpuStats;
        if (cpu?.CpuUsage is null || pre?.CpuUsage is null) return 0;
        if (cpu.SystemCpuUsage is null || pre.SystemCpuUsage is null) return 0;

        // Unsigned counters — guard the subtraction so a counter reset can't underflow.
        var cpuTotal = cpu.CpuUsage.TotalUsage;
        var preCpuTotal = pre.CpuUsage.TotalUsage;
        if (cpuTotal <= preCpuTotal) return 0;
        var systemTotal = cpu.SystemCpuUsage.Value;
        var preSystemTotal = pre.SystemCpuUsage.Value;
        if (systemTotal <= preSystemTotal) return 0;

        var cpuDelta = (double)(cpuTotal - preCpuTotal);
        var systemDelta = (double)(systemTotal - preSystemTotal);
        var onlineCpus = cpu.OnlineCpus is > 0 ? cpu.OnlineCpus.Value : 1;

        var percent = cpuDelta / systemDelta * onlineCpus * 100.0;
        return percent < 0 ? 0 : percent;
    }

    /// <summary>
    /// Real memory usage: <c>usage - inactive_file</c> when the detail is present (matches
    /// <c>docker stats</c>), else raw usage. Percent is against the limit when it is present and
    /// non-zero.
    /// </summary>
    private static (long Used, long? Limit, double? Percent) ComputeMemory(DockerContainerStats stats) {
        var mem = stats.MemoryStats;
        if (mem?.Usage is null) return (0, null, null);

        var usage = mem.Usage.Value;
        var inactive = mem.Stats?.InactiveFile ?? 0;
        var used = usage > inactive ? usage - inactive : usage;

        long? limit = mem.Limit is > 0 ? (long)mem.Limit.Value : null;
        double? percent = limit is > 0 ? (double)used / limit.Value * 100.0 : null;
        return ((long)used, limit, percent);
    }

    /// <summary>Strips the Docker-supplied leading slash from the first container name.</summary>
    private static string ContainerName(DockerContainerInfo c) {
        var raw = c.Names.Length > 0 ? c.Names[0] : c.Id;
        return raw.StartsWith('/') ? raw[1..] : raw;
    }

    // ── Host sampling (amendment F7) ──────────────────────────────────────────

    private async Task<HostSnapshot> BuildHostSnapshotAsync(CancellationToken ct) {
        if (string.IsNullOrEmpty(_hostProc)) {
            // No host /proc mount — host metrics unavailable, but disk may still fall back to df.
            var (dUsed, dTotal, dSource) = await ResolveDiskAsync(ct);
            return new HostSnapshot {
                Available = false,
                Reason = "host-proc-not-mounted",
                DiskUsedBytes = dUsed,
                DiskTotalBytes = dSource == "host-rootfs" ? dTotal : null,
                DiskPercent = dSource == "host-rootfs" ? Percent(dUsed, dTotal) : null,
                DiskSource = dSource,
                SampledAt = DateTimeOffset.UtcNow,
            };
        }

        var cpu = ReadHostCpuPercent();
        var (memUsed, memTotal, memPercent) = ReadHostMemory();
        var (load1, load5) = ReadHostLoad();
        var (diskUsed, diskTotal, diskSource) = await ResolveDiskAsync(ct);

        return new HostSnapshot {
            Available = true,
            Reason = null,
            CpuPercent = cpu.Percent,
            CpuCores = cpu.Cores,
            LoadAvg1 = load1,
            LoadAvg5 = load5,
            MemUsedBytes = memUsed,
            MemTotalBytes = memTotal,
            MemPercent = memPercent,
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskSource == "host-rootfs" ? diskTotal : null,
            DiskPercent = diskSource == "host-rootfs" ? Percent(diskUsed, diskTotal) : null,
            DiskSource = diskSource,
            SampledAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Reads <c>{hostProc}/stat</c> and computes aggregate CPU% from the delta against the previous
    /// tick. The first tick has no baseline ⇒ returns null percent (but a core count if derivable).
    /// </summary>
    private (double? Percent, int? Cores) ReadHostCpuPercent() {
        CpuTimes? current;
        int? cores;
        try {
            (current, cores) = ReadProcStat();
        } catch (Exception ex) {
            logger.LogDebug(ex, "Failed reading host /proc/stat");
            return (null, null);
        }
        if (current is null) return (null, cores);

        var prev = _prevCpuTimes;
        _prevCpuTimes = current;
        if (prev is null) return (null, cores); // need two reads for a delta

        var totalDelta = current.Total - prev.Total;
        var idleDelta = current.Idle - prev.Idle;
        if (totalDelta <= 0) return (null, cores);

        var busy = totalDelta - idleDelta;
        var percent = (double)busy / totalDelta * 100.0;
        return (Math.Clamp(percent, 0, 100), cores);
    }

    private (CpuTimes? Times, int? Cores) ReadProcStat() {
        var path = Path.Combine(_hostProc!, "stat");
        if (!File.Exists(path)) return (null, null);

        long total = 0, idle = 0;
        int coreCount = 0;
        var haveAggregate = false;
        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) break;
            // "cpu" = aggregate; "cpu0", "cpu1"… = per-core (count them for core total).
            if (line.StartsWith("cpu ", StringComparison.Ordinal)) {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // fields[0] = "cpu"; the rest are user nice system idle iowait irq softirq steal…
                long sum = 0;
                var idxIdle = 4; // user(1) nice(2) system(3) idle(4)
                for (var i = 1; i < fields.Length; i++) {
                    if (long.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) {
                        sum += v;
                        if (i == idxIdle) idle = v;
                    }
                }
                total = sum;
                haveAggregate = true;
            } else if (line.Length > 3 && char.IsDigit(line[3])) {
                coreCount++;
            }
        }
        return (haveAggregate ? new CpuTimes(total, idle) : null, coreCount > 0 ? coreCount : null);
    }

    /// <summary>Reads MemTotal/MemAvailable from <c>{hostProc}/meminfo</c> (kB → bytes).</summary>
    private (long? Used, long? Total, double? Percent) ReadHostMemory() {
        try {
            var path = Path.Combine(_hostProc!, "meminfo");
            if (!File.Exists(path)) return (null, null, null);

            long? totalKb = null, availableKb = null;
            foreach (var line in File.ReadLines(path)) {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    totalKb = ParseMeminfoKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    availableKb = ParseMeminfoKb(line);
                if (totalKb is not null && availableKb is not null) break;
            }
            if (totalKb is null || availableKb is null || totalKb.Value <= 0) return (null, null, null);

            var totalBytes = totalKb.Value * 1024;
            var availBytes = availableKb.Value * 1024;
            var usedBytes = totalBytes > availBytes ? totalBytes - availBytes : 0;
            return (usedBytes, totalBytes, (double)usedBytes / totalBytes * 100.0);
        } catch (Exception ex) {
            logger.LogDebug(ex, "Failed reading host /proc/meminfo");
            return (null, null, null);
        }
    }

    private static long? ParseMeminfoKb(string line) {
        // "MemTotal:       16384512 kB"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)
            ? kb
            : null;
    }

    /// <summary>Reads the 1- and 5-minute load averages from <c>{hostProc}/loadavg</c>.</summary>
    private (double? Load1, double? Load5) ReadHostLoad() {
        try {
            var path = Path.Combine(_hostProc!, "loadavg");
            if (!File.Exists(path)) return (null, null);
            var content = File.ReadAllText(path);
            var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double? l1 = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null;
            double? l5 = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : null;
            return (l1, l5);
        } catch (Exception ex) {
            logger.LogDebug(ex, "Failed reading host /proc/loadavg");
            return (null, null);
        }
    }

    /// <summary>
    /// Resolves disk usage: the host rootfs bind (via <see cref="DriveInfo"/>) when
    /// <c>WATCHTOWER_HOST_ROOTFS</c> is set — diskSource "host-rootfs"; otherwise the lazily-refreshed
    /// Docker <c>/system/df</c> total — diskSource "docker-df"; else unavailable.
    /// </summary>
    private async Task<(long? Used, long? Total, string Source)> ResolveDiskAsync(CancellationToken ct) {
        if (!string.IsNullOrEmpty(_hostRootfs)) {
            try {
                var drive = new DriveInfo(_hostRootfs);
                if (drive.IsReady) {
                    var total = drive.TotalSize;
                    var used = total - drive.TotalFreeSpace;
                    return (used, total, "host-rootfs");
                }
            } catch (Exception ex) {
                logger.LogDebug(ex, "Failed reading host rootfs disk at {Path}", _hostRootfs);
            }
        }

        var fallback = await GetDiskFallbackAsync(ct);
        return fallback is { } f
            ? (f.Used, f.Total, "docker-df")
            : (null, null, "unavailable");
    }

    /// <summary>
    /// Lazily refreshes the Docker df disk total at most every ~5 min (df is expensive). The df
    /// "total" doubles as both used and total here — Docker's df has no notion of free host space,
    /// so used == total keeps the percent at 100% only if we reported it; instead we surface the
    /// same value for both and let the client render bytes without a misleading percent.
    /// </summary>
    private async Task<(long Used, long Total)?> GetDiskFallbackAsync(CancellationToken ct) {
        var now = DateTimeOffset.UtcNow;
        if (_diskFallback is not null && now - _lastDiskFallback < DiskFallbackInterval)
            return _diskFallback;

        try {
            var df = await docker.GetSystemDfSummaryAsync(ct);
            var total = df.LayersSize + df.ContainersSizeRw + df.VolumesSize;
            _diskFallback = (total, total);
            _lastDiskFallback = now;
        } catch (Exception ex) {
            logger.LogDebug(ex, "Docker df disk fallback failed");
            // Keep any previously-cached value rather than dropping to unavailable.
        }
        return _diskFallback;
    }

    private static double? Percent(long? used, long? total)
        => used is not null && total is > 0 ? (double)used.Value / total.Value * 100.0 : null;

    /// <summary>Aggregate CPU jiffies snapshot from /proc/stat.</summary>
    private sealed record CpuTimes(long Total, long Idle);
}
