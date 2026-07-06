namespace Watchtower.Application.Config;

/// <summary>
/// Strongly-typed configuration options for Watchtower.
/// Bound from the "Watchtower" section of appsettings.json or environment variables
/// (e.g. WATCHTOWER__DBPATH, WATCHTOWER__DOCKERAPIVERSION).
/// </summary>
public sealed record WatchtowerOptions {
    /// <summary>Path to the SQLite database file.</summary>
    public string DbPath { get; init; } = "/data/watchtower.db";

    /// <summary>
    /// Docker Engine API version used for all Docker communication.
    /// <list type="bullet">
    ///   <item><description>
    ///     Direct API calls (<see cref="Services.DockerEngineClient"/>) use it as the URL segment: <c>/v1.43/containers/…</c>
    ///   </description></item>
    ///   <item><description>
    ///     <c>docker compose</c> subprocesses (<see cref="Services.ComposeCliService"/>) receive it via the
    ///     <c>DOCKER_API_VERSION</c> environment variable, preventing the compose CLI from
    ///     auto-negotiating a version newer than the daemon supports.
    ///   </description></item>
    /// </list>
    /// Update this if your Docker daemon supports a newer API version.
    /// </summary>
    public string DockerApiVersion { get; init; } = "1.43";

    /// <summary>
    /// When true, a background service periodically checks for a newer Watchtower image
    /// so the UI badge stays up to date without a manual check.
    /// Set via <c>WATCHTOWER__AUTOCHECKENABLED=true</c> or appsettings.json.
    /// Defaults to false so no outbound registry traffic is generated unless opted in.
    /// </summary>
    public bool AutoCheckEnabled { get; init; } = false;

    /// <summary>
    /// How often the background auto-check runs, in minutes. Clamped to 1–1440.
    /// Only relevant when <see cref="AutoCheckEnabled"/> is true.
    /// </summary>
    public int AutoCheckIntervalMinutes { get; init; } = 5;

    /// <summary>
    /// When true, a background service periodically checks whether any container image in
    /// each stack has a newer version available in the registry.
    /// Set via <c>WATCHTOWER__STACKCHECKENABLED=true</c> or appsettings.json.
    /// Defaults to false so no outbound registry traffic is generated unless opted in.
    /// </summary>
    public bool StackCheckEnabled { get; init; } = false;

    /// <summary>
    /// How often the stack update background check runs, in minutes. Clamped to 1–1440.
    /// Only relevant when <see cref="StackCheckEnabled"/> is true.
    /// </summary>
    public int StackCheckIntervalMinutes { get; init; } = 15;

    /// <summary>
    /// Metrics backend selection and its optional InfluxDB reader settings (ADR-0007).
    /// Bound from <c>WATCHTOWER__METRICS__*</c> (e.g. <c>WATCHTOWER__METRICS__BACKEND=influxdb</c>,
    /// <c>WATCHTOWER__METRICS__INFLUX__URL=…</c>).
    /// </summary>
    public MetricsOptions Metrics { get; init; } = new();
}

/// <summary>
/// Selects where the <c>metrics.*</c> handlers read from (ADR-0007). Defaults to the zero-dependency
/// in-memory sampler; set <see cref="Backend"/> to <c>influxdb</c> to read (including long-range
/// history) from an InfluxDB that an external collector — OpenTelemetry or Telegraf — populates.
/// The choice is applied at startup: only the selected backend's collection machinery is registered,
/// so switching backends requires a restart.
/// </summary>
public sealed record MetricsOptions {
    /// <summary>
    /// <c>memory</c> (default) — the in-memory ring buffer fed by the background sampler; or
    /// <c>influxdb</c> — read from InfluxDB, with the sampler disabled so there is a single collector.
    /// </summary>
    public string Backend { get; init; } = "memory";

    /// <summary>InfluxDB connection + schema mapping. Only used when <see cref="Backend"/> is <c>influxdb</c>.</summary>
    public InfluxOptions Influx { get; init; } = new();

    /// <summary>True when <see cref="Backend"/> selects the InfluxDB reader (case-insensitive).</summary>
    public bool UsesInflux => string.Equals(Backend, "influxdb", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// InfluxDB v2 connection and schema-mapping settings for the InfluxDB metrics backend. The schema
/// defaults track the OpenTelemetry <c>docker_stats</c>/<c>hostmetrics</c> semantic conventions the
/// collector emits; only <see cref="ComposeProjectTag"/> commonly needs changing (it depends on how the
/// collector was told to promote the compose-project label — see ADR-0007).
/// </summary>
public sealed record InfluxOptions {
    /// <summary>Base URL of the InfluxDB v2 server, e.g. <c>http://influxdb:8086</c>.</summary>
    public string? Url { get; init; }

    /// <summary>InfluxDB v2 organization the bucket belongs to.</summary>
    public string? Org { get; init; }

    /// <summary>Bucket the collector writes metrics into.</summary>
    public string? Bucket { get; init; }

    /// <summary>API token with read access to <see cref="Bucket"/>. Treated as a secret — never logged.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Tag name carrying the compose project, used for the per-stack rollup. <b>Opt-in: empty by
    /// default</b>, because referencing a tag the collector doesn't emit is a hard Flux error. Set this
    /// only after telling the collector to promote the compose-project label (docker_stats
    /// <c>container_labels_to_metric_labels: { com.docker.compose.project: compose_project }</c>), then
    /// set this to <c>compose_project</c>. Empty ⇒ per-stack rollup is empty (per-container and host still
    /// work).
    /// </summary>
    public string ComposeProjectTag { get; init; } = "";
}
