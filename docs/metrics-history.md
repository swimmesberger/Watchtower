# Metrics history (InfluxDB backend)

Watchtower's metrics come from a **pluggable backend** ([ADR-0007](decisions/0007-pluggable-metrics-backend.md)).
By default it uses the built-in in-memory sampler — zero dependencies, but only a ~15-minute live
window that is lost on restart. Point it at **InfluxDB** instead and the Dashboard reads from a durable
time-series store an external collector fills, so you can go back to *when an incident happened* and read
the utilization around it.

The two backends are **mutually exclusive** — exactly one collector is ever active:

| Backend | Source | History | Dependency |
| --- | --- | --- | --- |
| `memory` (default) | in-process sampler → in-memory ring | ~15 min | none (Docker socket) |
| `influxdb` | an external collector → InfluxDB, read back by Watchtower | as long as your bucket retains | the collector + InfluxDB |

When `influxdb` is selected, Watchtower runs **no sampler of its own** — InfluxDB is the single source of
truth. Switching backends requires a restart.

## Prerequisites

An external collector must already be writing host + container metrics into InfluxDB v2. The reference
setup is an OpenTelemetry Collector with the `hostmetrics` and `docker_stats` receivers and the `influxdb`
exporter — see [`deploy/monitoring/`](../deploy/monitoring) (or your existing Telegraf/OTel stack). Watchtower
is only a **reader**; it never writes metrics.

## Configuration

Bind via the `Watchtower:Metrics` config section or `WATCHTOWER__METRICS__*` environment variables:

| Env | Example | Purpose |
| --- | --- | --- |
| `WATCHTOWER__METRICS__BACKEND` | `influxdb` | `memory` (default) or `influxdb`. |
| `WATCHTOWER__METRICS__INFLUX__URL` | `http://influxdb:8086` | InfluxDB v2 base URL. |
| `WATCHTOWER__METRICS__INFLUX__ORG` | `my-org` | InfluxDB v2 organization. |
| `WATCHTOWER__METRICS__INFLUX__BUCKET` | `watchtower` | Bucket the collector writes into. |
| `WATCHTOWER__METRICS__INFLUX__TOKEN` | `‹token›` | API token with **read** access to the bucket (a secret — never logged). |
| `WATCHTOWER__METRICS__INFLUX__COMPOSEPROJECTTAG` | *(empty)* | **Opt-in** tag for the per-stack rollup. Empty ⇒ no per-stack grouping (per-container + host still work). Set to `compose_project` only after the collector emits it — see below. |
| `WATCHTOWER__METRICS__INFLUX__DISKMOUNTPOINT` | `/` | Mount point for the host-disk cell (matched against the `mountpoint` tag). On multi-volume hosts (e.g. Synology, where `/` is a small system partition) point at the data volume, e.g. `/volume2`. |

All four `INFLUX__*` connection values are required when the backend is `influxdb`; Watchtower fails fast
at startup if any is missing.

```yaml
services:
  watchtower:
    image: swimmes/watchtower:latest
    environment:
      WATCHTOWER__METRICS__BACKEND: influxdb
      WATCHTOWER__METRICS__INFLUX__URL: http://influxdb:8086
      WATCHTOWER__METRICS__INFLUX__ORG: my-org
      WATCHTOWER__METRICS__INFLUX__BUCKET: watchtower
      WATCHTOWER__METRICS__INFLUX__TOKEN: ${INFLUXDB_TOKEN}
```

## Expected schema

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

## Verifying your bucket's names

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

## Degradation

The InfluxDB backend fails soft, reusing the same `available`/`reason` path as host metrics:

- **InfluxDB unreachable** — host reads report `available: false`, reason `influx-unreachable`; container
  reads return empty. The Dashboard shows its "couldn't load" state.
- **No recent samples** — reason `influx-no-data` (the collector stopped, or the bucket is empty).

Everything else in Watchtower (deploy, logs, container inspection) is unaffected — only the metrics panels
depend on the store.
