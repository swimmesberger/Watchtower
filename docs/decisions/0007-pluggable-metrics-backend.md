# ADR-0007: Pluggable metrics backend — in-memory by default, InfluxDB opt-in

- Status: Accepted
- Date: 2026-07-06
- Related: [ADR-0002](0002-sqlite-via-ef-core.md) (the zero-external-dependency ethos), and
  [host-metrics.md](../host-metrics.md) (the opt-in host `/proc` mount this mirrors).

## Context

Watchtower samples host and per-container CPU/memory every ~10s into an in-memory ring buffer
(`MetricsStore`, ~90 samples ≈ 15 minutes), fed by a single background `MetricsSampler`. That is exactly
right for the Dashboard's live sparkline strip, but it is **ephemeral**: the history is lost on restart
and never spans more than ~15 minutes. You cannot go back to *when an incident happened* and read the
utilization around it.

Many operators already run a real time-series stack — an OpenTelemetry Collector or Telegraf scraping
host and container metrics into **InfluxDB** (or Prometheus). When they do, Watchtower's own sampler
becomes a **second collector sampling the same data**: two sources of truth for the same numbers, which
is ambiguous and wasteful.

We want durable, long-range history available *in the Dashboard* — without abandoning the
zero-dependency default that the single-container, single-SQLite-file design is built around.

## Decision

**Introduce a metrics backend abstraction (`IMetricsSource`) that the `metrics.*` handlers depend on,
with the concrete backend chosen by configuration at startup.**

Two implementations:

- **In-memory (default).** The existing `MetricsSampler` + `MetricsStore`. No external dependency; the
  ~15-minute live window; no long-range history.
- **InfluxDB (opt-in).** A read-only source that queries an InfluxDB an *external* collector
  (OTel/Telegraf) populates. Serves both the live window and long-range history via Flux
  `aggregateWindow` downsampling. Implemented as a thin HTTP + Flux client — no SDK dependency — to stay
  in keeping with the reflection-free, source-generated-JSON posture of the codebase.

The backend is selected by `WATCHTOWER__METRICS__BACKEND` (`memory` | `influxdb`, default `memory`).
**Only the selected backend's collection machinery is registered:** when `influxdb` is chosen,
`MetricsSampler`/`MetricsStore` are never registered, so Watchtower runs *no collector of its own* and
InfluxDB's collector is the single source of truth. Switching backends requires a restart — consistent
with how the host-metrics mounts already do.

A `metrics.capabilities` readout (source name, whether history is available, availability + reason) is
surfaced to the frontend so the time-range view is gated and failures degrade gracefully — the same
`available`/`reason` pattern host metrics already use.

## Consequences

- **The zero-dependency default is preserved.** Out of the box you still get the in-memory live strip
  with nothing but the Docker socket. Nothing about the default deployment changes.
- **No double collection.** Exactly one collector is active per deployment. Choosing InfluxDB *demotes*
  Watchtower to a pure consumer of the TSDB — resolving the two-sources-of-truth problem structurally,
  in the DI wiring, rather than by convention.
- **In InfluxDB mode, metrics become the one feature that depends on the external stack.** If the
  collector or InfluxDB is down, the metrics panels go blank (surfaced as `unavailable`), while deploy,
  logs, and container inspection stay zero-dependency.
- **The reader couples to the collector's schema.** It targets the OTel `docker_stats`/`hostmetrics`
  semantic conventions by default, with measurement/tag names left configurable for Telegraf or custom
  layouts. Per-stack history additionally requires the compose-project label to be carried into InfluxDB
  as a tag (`container_labels_to_metric_labels` on the receiver).
- **Host CPU%/RAM% are derived, not read.** OTel splits `system.cpu.*` / `system.memory.*` by a `state`
  tag, so a single percentage is computed in Flux (`1 − idle`, used/total) rather than read directly as
  the in-memory sampler does from `/proc`.

## Rejected alternatives

- **Replace the in-memory store outright with InfluxDB.** Breaks the zero-dependency default and adds an
  external dependency plus ingestion lag to the *live* view, which needs neither.
- **Persist history into SQLite ourselves** (rollup tables + downsampling + retention). Rebuilds a
  mini-TSDB inside the app; high-cardinality, high-frequency time-series is precisely what SQLite is poor
  at and what dedicated TSDBs exist for.
- **Run both collectors at once** — in-memory for live, InfluxDB for history. That is the exact
  two-sources-of-truth ambiguity this ADR sets out to remove.
- **Bundle VictoriaMetrics/Prometheus instead of reading the user's InfluxDB.** Fine stores, but the
  operator already runs InfluxDB. The abstraction lets any other reader be added later without touching
  the handlers.
