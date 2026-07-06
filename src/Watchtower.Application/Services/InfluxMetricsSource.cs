using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// The opt-in InfluxDB metrics backend (ADR-0007). Reads host and per-container series an external
/// collector (OpenTelemetry <c>docker_stats</c>/<c>hostmetrics</c>) writes into InfluxDB v2, and shapes
/// them into the same <see cref="HostSnapshot"/>/<see cref="ContainerReadout"/> the in-memory backend
/// produces. Serves both the live window and downsampled historical ranges via Flux
/// <c>aggregateWindow</c>.
///
/// <para>
/// Deliberately a thin HTTP + Flux client (POST <c>/api/v2/query</c>, parse the annotated CSV response) —
/// no InfluxDB SDK dependency. When Influx is unreachable or has no recent samples, host reads return an
/// <c>Unavailable</c> snapshot (reason <c>influx-unreachable</c> / <c>influx-no-data</c>) and container
/// reads return empty, so the UI degrades through the existing <c>available</c>/<c>reason</c> path.
/// </para>
///
/// <para>
/// <b>Schema (verified against a live OTel collector).</b> The default hostmetrics scrapers emit CPU as a
/// cumulative <c>system.cpu.time</c> counter (host CPU% is derived <c>1 − Δidle/Δtotal</c>) and memory as
/// <c>system.memory.usage</c> gauges split by <c>state</c> (RAM% is <c>used / Σstates</c>) — neither
/// <c>system.cpu.utilization</c> nor <c>system.memory.utilization</c> is present by default. Per-container
/// CPU% (<c>container.cpu.utilization</c>, already 0–100) and memory come straight through. Names live in
/// <see cref="Schema"/>; adjust there if your collector differs.
/// </para>
/// </summary>
public sealed class InfluxMetricsSource : IMetricsSource, IDisposable {
    private readonly HttpClient _http;
    private readonly ILogger<InfluxMetricsSource> _logger;
    private readonly string _bucket;
    private readonly string? _composeProjectTag;
    private readonly string _diskMountpoint;

    public MetricsCapabilities Capabilities { get; } = new("influxdb", HistoryAvailable: true);

    public InfluxMetricsSource(IOptions<WatchtowerOptions> options, ILogger<InfluxMetricsSource> logger) {
        _logger = logger;
        var influx = options.Value.Metrics.Influx;

        if (string.IsNullOrWhiteSpace(influx.Url)) throw new InvalidOperationException(
            "Metrics backend is 'influxdb' but WATCHTOWER__METRICS__INFLUX__URL is not set.");
        if (string.IsNullOrWhiteSpace(influx.Org)) throw new InvalidOperationException(
            "Metrics backend is 'influxdb' but WATCHTOWER__METRICS__INFLUX__ORG is not set.");
        if (string.IsNullOrWhiteSpace(influx.Bucket)) throw new InvalidOperationException(
            "Metrics backend is 'influxdb' but WATCHTOWER__METRICS__INFLUX__BUCKET is not set.");
        if (string.IsNullOrWhiteSpace(influx.Token)) throw new InvalidOperationException(
            "Metrics backend is 'influxdb' but WATCHTOWER__METRICS__INFLUX__TOKEN is not set.");

        _bucket = influx.Bucket!;
        // Opt-in: only reference the compose-project tag when the operator configured one (and thus told
        // the collector to emit it). Referencing a non-existent tag in a Flux pivot rowKey is a hard error.
        _composeProjectTag = string.IsNullOrWhiteSpace(influx.ComposeProjectTag) ? null : influx.ComposeProjectTag;
        _diskMountpoint = string.IsNullOrWhiteSpace(influx.DiskMountpoint) ? "/" : influx.DiskMountpoint;

        var baseUrl = influx.Url!.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri($"{baseUrl}/api/v2/query?org={Uri.EscapeDataString(influx.Org!)}") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", influx.Token);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/csv");
    }

    // ── Host ─────────────────────────────────────────────────────────────────

    public async ValueTask<HostReadout> GetHostAsync(MetricsWindow window, CancellationToken ct) {
        var (rangeStart, rangeStop, every) = ResolveRange(window);

        List<Dictionary<string, string>> rows;
        try {
            rows = await QueryAsync(HostFlux(rangeStart, rangeStop, every), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "InfluxDB host query failed");
            return new HostReadout(HostSnapshot.Unavailable("influx-unreachable"), []);
        }

        if (rows.Count == 0)
            return new HostReadout(HostSnapshot.Unavailable("influx-no-data"), []);

        var history = new List<HostSampleEntry>(rows.Count);
        foreach (var r in rows) {
            var t = ParseTime(r);
            if (t is null) continue;
            history.Add(new HostSampleEntry(t.Value, TryDouble(r, "cpu"), TryDouble(r, "memPct")));
        }
        history.Sort((a, b) => a.T.CompareTo(b.T));

        var last = rows[^1];
        var lastMemPct = TryDouble(last, "memPct");
        var lastMemUsed = TryDouble(last, "memUsed");
        long? memUsedBytes = lastMemUsed is { } mu ? (long)mu : null;
        long? memTotalBytes = memUsedBytes is { } used && lastMemPct is > 0
            ? (long)(used / (lastMemPct.Value / 100.0))
            : null;

        var (diskUsed, diskTotal) = await LookupDiskAsync(rangeStart, rangeStop, ct);

        var snapshot = new HostSnapshot {
            Available = true,
            Reason = null,
            CpuPercent = TryDouble(last, "cpu"),
            CpuCores = null,
            LoadAvg1 = TryDouble(last, "load1"),
            LoadAvg5 = TryDouble(last, "load5"),
            MemUsedBytes = memUsedBytes,
            MemTotalBytes = memTotalBytes,
            MemPercent = lastMemPct,
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskTotal,
            DiskPercent = diskUsed is { } du && diskTotal is > 0 ? (double)du / diskTotal.Value * 100.0 : null,
            DiskSource = diskUsed is not null ? "influx" : "unavailable",
            SampledAt = history.Count > 0 ? history[^1].T : DateTimeOffset.UtcNow,
        };
        return new HostReadout(snapshot, history);
    }

    // ── Containers ─────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ContainerReadout>> GetContainersAsync(MetricsWindow window, CancellationToken ct) {
        var (rangeStart, rangeStop, every) = ResolveRange(window);

        List<Dictionary<string, string>> rows;
        try {
            rows = await QueryAsync(ContainerFlux(rangeStart, rangeStop, every), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "InfluxDB container query failed");
            return [];
        }

        // Optional per-stack lookup: container name → compose project. Kept out of the metrics query's
        // pivot rowKey on purpose — a pivot rowKey column must exist in every series, and points from
        // before the collector started emitting the tag would fail the whole query. keep() here simply
        // omits the column when absent, so a partial/failed lookup only leaves StackName null.
        var projects = await LookupProjectsAsync(rangeStart, rangeStop, ct);

        // Group flat rows into per-container rings, keyed by container name (stable across restarts,
        // unlike the container id).
        var byContainer = new Dictionary<string, ContainerAccumulator>(StringComparer.Ordinal);
        foreach (var r in rows) {
            if (!r.TryGetValue(Schema.ContainerNameTag, out var name) || string.IsNullOrEmpty(name)) continue;
            var t = ParseTime(r);
            if (t is null) continue;

            if (!byContainer.TryGetValue(name, out var acc)) {
                acc = new ContainerAccumulator();
                if (projects is not null && projects.TryGetValue(name, out var project)) acc.Project = project;
                byContainer[name] = acc;
            }
            var cpu = TryDouble(r, Schema.ContainerCpuMeasurement) ?? 0;
            var mem = TryDouble(r, Schema.ContainerMemMeasurement) ?? 0;
            acc.MemPercent = TryDouble(r, Schema.ContainerMemPercentMeasurement);
            acc.MemLimit = TryDouble(r, Schema.ContainerMemLimitMeasurement) is { } lim ? (long)lim : null;
            acc.History.Add(new ContainerSampleEntry(t.Value, cpu, (long)mem));
        }

        var result = new List<ContainerReadout>(byContainer.Count);
        foreach (var (name, acc) in byContainer) {
            if (acc.History.Count == 0) continue;
            acc.History.Sort((a, b) => a.T.CompareTo(b.T));
            var latest = acc.History[^1];
            var snapshot = new ContainerSnapshot {
                ContainerId = name, // no stable id column carried — name is the identity here
                ContainerName = name,
                StackName = acc.Project,
                CpuPercent = latest.CpuPercent,
                MemUsedBytes = latest.MemUsedBytes,
                MemLimitBytes = acc.MemLimit,
                MemPercent = acc.MemPercent,
                Online = true,
                SampledAt = latest.T,
            };
            result.Add(new ContainerReadout(snapshot, acc.History));
        }
        return result;
    }

    private sealed class ContainerAccumulator {
        public readonly List<ContainerSampleEntry> History = [];
        public string? Project;
        public double? MemPercent;
        public long? MemLimit;
    }

    // ── Flux ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-container CPU% (already 0–100) + memory bytes/percent/limit, downsampled to
    /// <paramref name="every"/> and pivoted by container name. The compose-project tag is intentionally
    /// not in the rowKey (see <see cref="LookupProjectsAsync"/>).
    /// </summary>
    private string ContainerFlux(string rangeStart, string rangeStop, string every) =>
        $$"""
        from(bucket: "{{_bucket}}")
          |> range(start: {{rangeStart}}, stop: {{rangeStop}})
          |> filter(fn: (r) => r._measurement == "{{Schema.ContainerCpuMeasurement}}" or r._measurement == "{{Schema.ContainerMemMeasurement}}" or r._measurement == "{{Schema.ContainerMemPercentMeasurement}}" or r._measurement == "{{Schema.ContainerMemLimitMeasurement}}")
          |> filter(fn: (r) => r._field == "{{Schema.FieldKeyGauge}}")
          |> aggregateWindow(every: {{every}}, fn: mean, createEmpty: false)
          |> pivot(rowKey: ["_time", "{{Schema.ContainerNameTag}}"], columnKey: ["_measurement"], valueColumn: "_value")
          |> keep(columns: ["_time", "{{Schema.ContainerNameTag}}", "{{Schema.ContainerCpuMeasurement}}", "{{Schema.ContainerMemMeasurement}}", "{{Schema.ContainerMemPercentMeasurement}}", "{{Schema.ContainerMemLimitMeasurement}}"])
        """;

    /// <summary>
    /// Resolves the container-name → compose-project map for the per-stack rollup, or null when no
    /// compose tag is configured. Uses <c>last()</c> + <c>keep()</c> (not a pivot rowKey) so series that
    /// lack the tag are simply omitted rather than failing the query. A failure returns null (StackName
    /// stays unset) rather than dropping container metrics.
    /// </summary>
    private async Task<Dictionary<string, string>?> LookupProjectsAsync(string rangeStart, string rangeStop, CancellationToken ct) {
        if (_composeProjectTag is null) return null;
        var flux = $$"""
        from(bucket: "{{_bucket}}")
          |> range(start: {{rangeStart}}, stop: {{rangeStop}})
          |> filter(fn: (r) => r._measurement == "{{Schema.ContainerCpuMeasurement}}" and r._field == "{{Schema.FieldKeyGauge}}")
          |> last()
          |> keep(columns: ["{{Schema.ContainerNameTag}}", "{{_composeProjectTag}}"])
          |> group()
        """;
        try {
            var rows = await QueryAsync(flux, ct);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in rows) {
                if (r.TryGetValue(Schema.ContainerNameTag, out var name) && !string.IsNullOrEmpty(name)
                    && r.TryGetValue(_composeProjectTag, out var project) && !string.IsNullOrEmpty(project))
                    map[name] = project;
            }
            return map;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "InfluxDB container-project lookup failed; per-stack rollup skipped this tick");
            return null;
        }
    }

    /// <summary>
    /// Host CPU% (derived <c>1 − Δidle/Δtotal</c> from the <c>system.cpu.time</c> counter across all
    /// cores/states), memory used% + used-bytes (<c>used / Σstates</c> of <c>system.memory.usage</c>), and
    /// 1m/5m load — each reduced to (_time,_value) with a synthetic <c>_m</c> label and pivoted into one
    /// row per bucket. Verified against a live OTel <c>hostmetrics</c> collector.
    /// </summary>
    private string HostFlux(string rangeStart, string rangeStop, string every) =>
        $$"""
        cpuBase = from(bucket: "{{_bucket}}")
          |> range(start: {{rangeStart}}, stop: {{rangeStop}})
          |> filter(fn: (r) => r._measurement == "{{Schema.HostCpuTimeMeasurement}}" and r._field == "{{Schema.FieldKeyCounter}}")
          |> aggregateWindow(every: {{every}}, fn: last, createEmpty: false)
          |> difference(nonNegative: true)
        cidle = cpuBase |> filter(fn: (r) => r.state == "idle") |> group(columns: ["_time"]) |> sum() |> set(key: "_k", value: "i")
        ctot = cpuBase |> group(columns: ["_time"]) |> sum() |> set(key: "_k", value: "t")
        cpu = union(tables: [cidle, ctot])
          |> pivot(rowKey: ["_time"], columnKey: ["_k"], valueColumn: "_value")
          |> map(fn: (r) => ({ _time: r._time, _value: (1.0 - r.i / r.t) * 100.0, _m: "cpu" }))
          |> group()
        mem = from(bucket: "{{_bucket}}")
          |> range(start: {{rangeStart}}, stop: {{rangeStop}})
          |> filter(fn: (r) => r._measurement == "{{Schema.HostMemUsageMeasurement}}" and r._field == "{{Schema.FieldKeyGauge}}")
          |> aggregateWindow(every: {{every}}, fn: last, createEmpty: false)
          |> pivot(rowKey: ["_time"], columnKey: ["state"], valueColumn: "_value")
        memPct = mem
          |> map(fn: (r) => ({ _time: r._time, _value: float(v: r.used) / float(v: r.used + r.free + r.buffered + r.cached + r.slab_reclaimable + r.slab_unreclaimable) * 100.0, _m: "memPct" }))
          |> group()
        memUsed = mem
          |> map(fn: (r) => ({ _time: r._time, _value: float(v: r.used), _m: "memUsed" }))
          |> group()
        load1 = from(bucket: "{{_bucket}}")
          |> range(start: {{rangeStart}}, stop: {{rangeStop}})
          |> filter(fn: (r) => r._measurement == "{{Schema.HostLoad1Measurement}}" and r._field == "{{Schema.FieldKeyGauge}}")
          |> aggregateWindow(every: {{every}}, fn: mean, createEmpty: false)
          |> keep(columns: ["_time", "_value"]) |> set(key: "_m", value: "load1") |> group()
        load5 = from(bucket: "{{_bucket}}")
          |> range(start: {{rangeStart}}, stop: {{rangeStop}})
          |> filter(fn: (r) => r._measurement == "{{Schema.HostLoad5Measurement}}" and r._field == "{{Schema.FieldKeyGauge}}")
          |> aggregateWindow(every: {{every}}, fn: mean, createEmpty: false)
          |> keep(columns: ["_time", "_value"]) |> set(key: "_m", value: "load5") |> group()
        union(tables: [cpu, memPct, memUsed, load1, load5])
          |> pivot(rowKey: ["_time"], columnKey: ["_m"], valueColumn: "_value")
          |> sort(columns: ["_time"])
        """;

    /// <summary>
    /// Latest used/total bytes for the configured disk mount point (<c>system.filesystem.usage</c> summed
    /// over its <c>used</c>/<c>free</c>/<c>reserved</c> states). Returns (null, null) when the mount point
    /// isn't present or the query fails, so the disk cell degrades rather than erroring the host read.
    /// </summary>
    private async Task<(long? Used, long? Total)> LookupDiskAsync(string rangeStart, string rangeStop, CancellationToken ct) {
        var flux = $$"""
        from(bucket: "{{_bucket}}")
          |> range(start: {{rangeStart}}, stop: {{rangeStop}})
          |> filter(fn: (r) => r._measurement == "{{Schema.HostFsUsageMeasurement}}" and r._field == "{{Schema.FieldKeyGauge}}" and r.mountpoint == "{{_diskMountpoint}}")
          |> last()
          |> pivot(rowKey: ["mountpoint"], columnKey: ["state"], valueColumn: "_value")
          |> map(fn: (r) => ({ _time: now(), used: float(v: r.used), total: float(v: r.used + r.free + r.reserved) }))
          |> keep(columns: ["used", "total"])
        """;
        try {
            var rows = await QueryAsync(flux, ct);
            if (rows.Count == 0) return (null, null);
            var used = TryDouble(rows[^1], "used");
            var total = TryDouble(rows[^1], "total");
            return (used is { } u ? (long)u : null, total is { } t ? (long)t : null);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "InfluxDB disk lookup failed for mount point {Mount}", _diskMountpoint);
            return (null, null);
        }
    }

    /// <summary>
    /// Resolves a <see cref="MetricsWindow"/> to Flux <c>range()</c> bounds and an <c>aggregateWindow</c>
    /// step. Live ⇒ the last 15 minutes at 10s (≈90 points, matching the in-memory ring's sparkline).
    /// </summary>
    private static (string Start, string Stop, string Every) ResolveRange(MetricsWindow window) {
        if (!window.IsHistory) return ("-15m", "now()", "10s");
        var start = window.From!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var stop = window.To!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var seconds = Math.Max(1, (int)(window.Step ?? TimeSpan.FromMinutes(1)).TotalSeconds);
        return (start, stop, $"{seconds}s");
    }

    // ── HTTP + annotated-CSV parsing ────────────────────────────────────────────

    private async Task<List<Dictionary<string, string>>> QueryAsync(string flux, CancellationToken ct) {
        using var content = new StringContent(flux, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.flux");

        using var resp = await _http.PostAsync((Uri?)null, content, ct);
        resp.EnsureSuccessStatusCode();
        var csv = await resp.Content.ReadAsStringAsync(ct);
        return ParseAnnotatedCsv(csv);
    }

    /// <summary>
    /// Parses InfluxDB's annotated-CSV response into flat rows keyed by column name. Annotation lines
    /// (<c>#…</c>) are skipped; the first non-annotation line of each table block is its header; blank
    /// lines separate table blocks (column order can differ between blocks, so rows are keyed by name).
    /// Handles RFC4180 quoting.
    /// </summary>
    private static List<Dictionary<string, string>> ParseAnnotatedCsv(string csv) {
        var rows = new List<Dictionary<string, string>>();
        string[]? header = null;

        foreach (var rawLine in csv.Split('\n')) {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) { header = null; continue; }   // table separator
            if (line[0] == '#') continue;                        // annotation

            var cols = SplitCsvLine(line);
            if (header is null) { header = cols; continue; }     // header row

            var row = new Dictionary<string, string>(header.Length, StringComparer.Ordinal);
            for (var i = 0; i < header.Length && i < cols.Length; i++) {
                if (!string.IsNullOrEmpty(header[i])) row[header[i]] = cols[i];
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string[] SplitCsvLine(string line) {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++) {
            var c = line[i];
            if (inQuotes) {
                if (c == '"') {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                } else sb.Append(c);
            } else if (c == '"') {
                inQuotes = true;
            } else if (c == ',') {
                fields.Add(sb.ToString());
                sb.Clear();
            } else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return [.. fields];
    }

    private static DateTimeOffset? ParseTime(Dictionary<string, string> row) =>
        row.TryGetValue("_time", out var s)
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t
            : null;

    private static double? TryDouble(Dictionary<string, string> row, string column) =>
        row.TryGetValue(column, out var s)
        && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Measurement / field / tag names the queries assume, verified against a live OpenTelemetry collector
    /// (influxdb exporter, default schema: field key <c>gauge</c> for gauges, <c>counter</c> for counters).
    /// Adjust here if your collector's schema differs.
    /// </summary>
    private static class Schema {
        public const string FieldKeyGauge = "gauge";
        public const string FieldKeyCounter = "counter";

        // docker_stats receiver
        public const string ContainerNameTag = "container.name";
        public const string ContainerCpuMeasurement = "container.cpu.utilization";       // percent 0–100
        public const string ContainerMemMeasurement = "container.memory.usage.total";    // bytes
        public const string ContainerMemPercentMeasurement = "container.memory.percent"; // percent
        public const string ContainerMemLimitMeasurement = "container.memory.usage.limit"; // bytes

        // hostmetrics receiver — defaults emit cpu.time (counter) + memory.usage (gauge by state),
        // NOT *.utilization; host CPU%/RAM% are derived in HostFlux.
        public const string HostCpuTimeMeasurement = "system.cpu.time";            // counter, by state+cpu
        public const string HostMemUsageMeasurement = "system.memory.usage";       // bytes, by state
        public const string HostLoad1Measurement = "system.cpu.load_average.1m";
        public const string HostLoad5Measurement = "system.cpu.load_average.5m";
        public const string HostFsUsageMeasurement = "system.filesystem.usage"; // bytes, by state+mountpoint
    }
}
