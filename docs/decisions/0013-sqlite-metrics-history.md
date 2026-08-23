# ADR-0013: SQLite-persisted metrics history by default, runtime-switchable backend

- Status: Accepted
- Date: 2026-08-10
- Amends: [ADR-0007](0007-pluggable-metrics-backend.md) (pluggable metrics backend). The abstraction
  and the InfluxDB reader stay; the "SQLite persistence rejected", "only the selected backend's
  machinery is registered", and "switching requires a restart" decisions are superseded.

> **Renamed by [ADR-0024](0024-postgresql-only-and-state-in-the-database.md) (2026-08-23).** The backend
> this ADR calls `sqlite` is now called `database`, because Watchtower's own database is PostgreSQL.
> Nothing else changed: the same sampler writes the same two tiers into the same tables, and it is still
> the default. The old value is still accepted on read, so a stored or env-pinned `sqlite` keeps working;
> read every `sqlite` below as `database`.

## Context

ADR-0007 gave Watchtower two metrics backends: the zero-dependency in-memory ring (default, ~15 min,
lost on restart) and an opt-in InfluxDB reader fed by an external OTel/Telegraf collector. Operating
that split taught us two things:

1. **The external-collector contract is fragile in practice.** The reader couples to the collector's
   schema, and the collector's defaults move: `docker_stats` renamed its CPU metric
   (`container.cpu.percent` → `container.cpu.utilization`) across collector releases, and a bucket
   filled by the "wrong" version renders every CPU graph as a silent, plausible-looking flat 0 while
   memory works. The failure mode is not an error — it is wrong data.
2. **History shouldn't require running a time-series stack.** InfluxDB idles at hundreds of MB of
   RAM. For the single-node deployments Watchtower targets (ADR-0010), "go back to when the incident
   happened" is a core feature that should not cost an extra container fleet — nor any setup at all.

ADR-0007 rejected SQLite persistence as "rebuilding a mini-TSDB". That rejection assumed persisting
the sampler's **raw 10s firehose**. It does not hold for what the Dashboard actually needs:
pre-aggregated minute samples for a handful of series (one host + dozens of containers) with a
bounded retention window. At that shape the math is trivial for SQLite — a 30-day window for 20
containers is a few hundred thousand small rows, a few tens of MB — and Watchtower already owns a
SQLite file, a sampler producing exactly the right numbers, and a migration story (ADR-0002).

Separately, the backend choice was boot-fixed in DI, so trying history out meant editing compose and
restarting — needless friction for what is now the default experience.

## Decision

**Three backends, selected by `Watchtower:Metrics:Backend`, switchable at runtime:**

| Backend | Source of truth | History | Dependency |
| --- | --- | --- | --- |
| `sqlite` (**new default**) | in-process sampler → ring (live) + SQLite (history) | retention window, default 30 days | none |
| `memory` (opt-in) | in-process sampler → ring only | ~15 min live window | none |
| `influxdb` (opt-in, BYO) | external collector → InfluxDB | bucket retention | collector + InfluxDB |

1. **SQLite persistence is two-tier and windowed.** The sampler keeps filling the 10s in-memory ring
   for the live Dashboard strip (unchanged). Under the `sqlite` backend it additionally aggregates
   each minute into one row per series (host, and per container), and periodically rolls minute rows
   older than ~3 days up into 10-minute rows. Minute rows are deleted past the raw window; rollup
   rows past `Watchtower:Metrics:RetentionDays` (default 30, clamped 1–365). Historical queries
   aggregate whichever tier covers the range into the requested step buckets in SQL. No raw 10s
   samples are ever persisted — the "mini-TSDB" ADR-0007 feared is exactly what this is not.
2. **The backend is resolved per call, not per boot.** `IMetricsSource` is registered once as a
   router that delegates to the backend named by the *current* options snapshot. The sampler, store,
   and SQLite reader are always registered; the sampler checks the backend each tick — it skips
   sampling entirely under `influxdb` (preserving ADR-0007's single-collector invariant) and skips
   persistence under `memory`. The InfluxDB reader is constructed lazily from the current options and
   rebuilt when they change; invalid connection settings degrade to `unavailable` instead of
   throwing at boot. Because `Watchtower:*` settings written through the Elarion settings store layer
   over env/appsettings and re-bind `IOptionsMonitor` live (the automation-toggles mechanism), a
   `metrics.updateConfig` handler makes the whole switch a Settings-page action — no restart, no
   compose edit. The `metrics-history` client flag is already evaluated per `elarion.session` call
   and follows the switch; the frontend re-fetches its boot snapshot via a reload after saving.
3. **InfluxDB remains the bring-your-own integration path** for operators who already run an
   observability stack and want Grafana over the same data. It is no longer the only road to history,
   so its schema coupling stops being a default-experience risk.

## Consequences

- **History works out of the box.** A fresh install (and any upgraded install that never set
  `Backend`) persists history with zero configuration and zero new dependencies. Operators who want
  the old write-free behaviour set `memory`.
- **The default write load is bounded and small.** One row per series per minute, single batched
  insert per flush; retention and rollup ride the sampler loop (this codebase deliberately has no
  separate job scheduler — sweeps ride existing loops, like auth-session expiry).
- **Restart gaps are honest.** While Watchtower is down nothing samples, so history has holes. The
  chart renders gaps (null points) rather than interpolating over them. The external-collector
  backend keeps collecting through Watchtower restarts — one reason BYO Influx stays.
- **DB size is governed.** Retention is enforced by deletion windows, and metrics rows live in the
  existing `watchtower.db`. At default settings the steady-state footprint is tens of MB; the
  retention knob is the escape valve.
- **ADR-0007's mutual-exclusion wording is obsolete.** Registration is no longer conditional;
  exclusivity of *collection* is behavioural (the sampler's per-tick backend check). Doc comments
  that said "exactly one implementation is registered" / "switching requires a restart" are updated
  by this ADR.

## Rejected alternatives

- **Watchtower-managed InfluxDB/VictoriaMetrics containers** (provision the stack over the Docker
  socket, CaddyManager-style). Solves the schema drift but keeps the RAM cost and adds container
  lifecycle management for a default feature. Remains open as a future opt-in for Grafana users;
  SQLite covers the product need without it.
- **Persisting the raw 10s stream with query-time downsampling.** ~50× the rows for detail nobody
  reads at week scale; this is the shape ADR-0007 rightly rejected.
- **A second always-on collector writing SQLite while Influx serves reads.** Recreates the
  two-sources-of-truth ambiguity ADR-0007 removed; the per-tick backend check keeps one collector.
- **Reactive capability snapshot instead of reload-after-switch.** Needs upstream Elarion work
  (`@swimmesberger/elarion-contributions` takes a static capability object; there is no update API).
  Filed as an Elarion enhancement; a reload after an explicit settings save is acceptable UX.
