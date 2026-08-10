# Metrics history

Watchtower's metrics come from a **pluggable backend** ([ADR-0007](decisions/0007-pluggable-metrics-backend.md),
amended by [ADR-0013](decisions/0013-sqlite-metrics-history.md)). Since ADR-0013 history is **on by
default**: the built-in sampler persists downsampled utilization into Watchtower's own SQLite database,
so the History view can go back to *when an incident happened* with zero configuration and zero extra
containers.

| Backend | Source | History | Dependency |
| --- | --- | --- | --- |
| `sqlite` (**default**) | in-process sampler → live ring + SQLite | retention window (default 30 days) | none (Docker socket) |
| `memory` (opt-in) | in-process sampler → in-memory ring | ~15 min live window, nothing written | none (Docker socket) |
| `influxdb` (opt-in, BYO) | an external collector → InfluxDB, read back by Watchtower | as long as your bucket retains | the collector + InfluxDB |

Exactly one **collector** is ever active: under `influxdb` the built-in sampler idles and InfluxDB is
the single source of truth. The backend is **runtime-switchable** — change it under *Settings →
Metrics* (or via `metrics.updateConfig`) and it applies immediately, no restart. Values set through the
UI persist in the settings store and layer over the environment defaults below.

## Configuration

Bind via the `Watchtower:Metrics` config section or `WATCHTOWER__METRICS__*` environment variables
(the Settings page writes the same keys at runtime):

| Env | Example | Purpose |
| --- | --- | --- |
| `WATCHTOWER__METRICS__BACKEND` | `sqlite` | `sqlite` (default), `memory`, or `influxdb`. |
| `WATCHTOWER__METRICS__RETENTIONDAYS` | `30` | History window of the `sqlite` backend, 1–365 days. |
| `WATCHTOWER__METRICS__INFLUX__URL` | `http://influxdb:8086` | InfluxDB v2 base URL. |
| `WATCHTOWER__METRICS__INFLUX__ORG` | `my-org` | InfluxDB v2 organization. |
| `WATCHTOWER__METRICS__INFLUX__BUCKET` | `watchtower` | Bucket the collector writes into. |
| `WATCHTOWER__METRICS__INFLUX__TOKEN` | `‹token›` | API token with **read** access to the bucket (a secret — never logged). |
| `WATCHTOWER__METRICS__INFLUX__COMPOSEPROJECTTAG` | *(empty)* | **Opt-in** tag for the per-stack rollup. Empty ⇒ no per-stack grouping (per-container + host still work). Set to `compose_project` only after the collector emits it — see below. |
| `WATCHTOWER__METRICS__INFLUX__DISKMOUNTPOINT` | `/` | Mount point for the host-disk cell (matched against the `mountpoint` tag). On multi-volume hosts (e.g. Synology, where `/` is a small system partition) point at the data volume, e.g. `/volume2`. |

The four `INFLUX__*` connection values are required for the `influxdb` backend; the
`metrics.updateConfig` handler rejects the switch without them, and a backend configured `influxdb` via
environment with missing values serves `unavailable` (reason `influx-misconfigured`) until fixed.

## The sqlite backend (default)

How it stores (ADR-0013): the live Dashboard strip still comes from the 10-second in-memory ring; in
parallel each minute is averaged into one row per series, and minutes older than ~3 days are rolled up
into 10-minute rows kept for `RETENTIONDAYS`. Older rows are deleted on a background sweep. At default
settings the steady-state footprint for a few dozen containers is tens of MB inside the existing
`watchtower.db`.

Honest limitations:

- **Sampling stops while Watchtower is down** — restarts leave gaps in the charts (rendered as gaps,
  not zeros). If you need collection that survives Watchtower restarts, use the `influxdb` backend
  with an external collector.
- Host CPU/RAM/load still need the opt-in `/proc` mount (see [host-metrics.md](host-metrics.md));
  without it only container series are persisted.

## The influxdb backend (bring your own)

An external collector must already be writing host + container metrics into InfluxDB v2. The reference
setup is an OpenTelemetry Collector with the `hostmetrics` and `docker_stats` receivers and the
`influxdb` exporter. Watchtower is only a **reader**; it never writes metrics. Choose this when you
already run an observability stack (e.g. with Grafana on the same data), or when you need collection
to continue while Watchtower itself is down.

### Expected schema

The queries target the **OpenTelemetry influxdb exporter's default layout** — measurement = metric name,
field key `gauge` for gauges and `counter` for cumulative counters. These names are **verified against a
live OTel `hostmetrics`+`docker_stats` collector**. If your collector differs, adjust
`InfluxMetricsSource.Schema` (`src/Watchtower.Application/Services/InfluxMetricsSource.cs`).

| Reading | Measurement (field) | Derivation |
| --- | --- | --- |
| Container CPU % | `container.cpu.utilization` (gauge) | already 0–100 |
| Container memory | `container.memory.usage.total` / `.percent` / `.usage.limit` (gauge) | direct |
| Host CPU % | `system.cpu.time` (counter, by `state`+`cpu`) | `1 − Δidle/Δtotal` across cores |
| Host memory % / bytes | `system.memory.usage` (gauge, by `state`) | `used / Σstates` |
| Host load 1m / 5m | `system.cpu.load_average.1m` / `.5m` (gauge) | direct |
| Host disk % / bytes | `system.filesystem.usage` (gauge, by `state`+`mountpoint`) | `used / (used+free+reserved)` for the configured mount point |

> The default hostmetrics scrapers emit **no** `system.cpu.utilization` or `system.memory.utilization`,
> so host CPU%/RAM% are derived in Flux from the counter/state series above. Host disk uses the mount
> point set by `DISKMOUNTPOINT` (default `/`).

> **Collector version matters.** Older `opentelemetry-collector-contrib` releases emitted
> `container.cpu.percent` instead of `container.cpu.utilization` (or shipped the latter disabled by
> default). A bucket filled by such a collector renders every container CPU graph as a flat 0 while
> memory works — Watchtower logs a warning when it detects this shape. Pin the collector image and
> enable the metric explicitly:
>
> ```yaml
> receivers:
>   docker_stats:
>     metrics:
>       container.cpu.utilization:
>         enabled: true
> ```

**Per-stack history needs the compose-project label carried into InfluxDB** as a tag. On the `docker_stats`
receiver:

```yaml
docker_stats:
  container_labels_to_metric_labels:
    com.docker.compose.project: compose_project
```

Per-container and host readings work without it. Host **disk** is not yet mapped from InfluxDB (the
Dashboard disk cell shows unavailable in this backend) — filesystem series need per-mount resolution;
tracked as follow-up in ADR-0007.

### Verifying your bucket's names

Before trusting the mapping, confirm the measurement and field names in **Data Explorer** or via the CLI:

```
# measurements present in the bucket
import "influxdata/influxdb/schema"
schema.measurements(bucket: "watchtower")

# field keys + tag keys for one measurement
schema.measurementFieldKeys(bucket: "watchtower", measurement: "container.cpu.utilization")
schema.measurementTagKeys(bucket: "watchtower", measurement: "container.cpu.utilization")
```

If the measurement names or the `gauge` field key differ from the table above, update
`InfluxMetricsSource.Schema` to match.

## How the UI knows

The frontend learns whether history is available from the framework's `elarion.session` capability
snapshot: the Metrics module exposes a **`metrics-history`** client flag (`[ClientFeatures]`, Elarion
ADR-0030) that is true on the `sqlite` and `influxdb` backends. The flag is evaluated per session fetch
against the routed backend, so it follows a runtime switch; the Settings page reloads after a switch
that changes it, which rebuilds the nav. On the `memory` backend the History item doesn't render, and a
direct URL hit shows an "enable in Settings" banner.

## Degradation

Every backend fails soft through the same `available`/`reason` path:

- **`sqlite` query failure** — reason `sqlite-history-error`; the live strip is unaffected.
- **InfluxDB unreachable** — reason `influx-unreachable`; container reads return empty.
- **InfluxDB selected but connection settings missing** — reason `influx-misconfigured`.
- **No recent samples** — reason `influx-no-data` (the collector stopped, or the bucket is empty).

Everything else in Watchtower (deploy, logs, container inspection) is unaffected — only the metrics
panels depend on the store.
