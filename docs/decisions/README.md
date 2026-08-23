# Architecture Decision Records

This directory holds Architecture Decision Records (ADRs) for Watchtower, in the classic
[Nygard format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

An ADR captures a single architecturally significant decision: the context that forced it, the decision
itself, and the consequences we accept. ADRs are append-only history — once an ADR is `Accepted`, prefer
writing a new ADR that supersedes it over editing the original.

The style follows the [Elarion ADRs](https://github.com/swimmesberger/Elarion/tree/main/docs/decisions),
the framework Watchtower is built on; framework-level decisions live there, application-level decisions
live here.

## Conventions

- File name: `NNNN-kebab-case-title.md` (zero-padded sequence number).
- Status: `Proposed` | `Accepted` | `Superseded by ADR-NNNN` | `Deprecated`.
- Sections: Status, Context, Decision, Consequences. Add others (Options, Rejected alternatives,
  References) when they add value.

## Index

- [ADR-0001: Rebuild Watchtower on the Elarion framework](0001-rebuild-on-elarion.md)
- [ADR-0002: SQLite via EF Core; drop NativeAOT](0002-sqlite-via-ef-core.md)
- [ADR-0003: JSON-RPC is the primary transport; streaming and webhooks stay plain HTTP](0003-jsonrpc-primary-transport.md)
- [ADR-0004: Singleton services access EF Core through `IServiceScopeFactory`](0004-singleton-ef-scopes.md)
- [ADR-0005: Development orchestration with a .NET Aspire AppHost](0005-aspire-dev-orchestration.md)
- [ADR-0006: The web frontend is a NoTargets project in the solution](0006-frontend-notargets-project.md)
- [ADR-0007: Pluggable metrics backend — in-memory by default, InfluxDB opt-in](0007-pluggable-metrics-backend.md)
- [ADR-0008: Deployed applications query themselves through a token-authenticated REST API](0008-public-app-api.md)
- [ADR-0009: A management stack manages one template's tenants through a granted REST API](0009-public-management-api.md)
- [ADR-0010: The future runtime target is the Kubernetes API, via KubeSolo](0010-target-kubesolo-runtime.md)
- [ADR-0011: A stack may ask which sibling tenants the proven visiting user can reach](0011-user-scoped-tenant-discovery.md)
- [ADR-0012: Injected variables reach containers directly, through a generated compose override](0012-direct-env-injection.md)
- [ADR-0013: SQLite-persisted metrics history by default, runtime-switchable backend](0013-sqlite-metrics-history.md)
- [ADR-0014: Environment variables win over runtime settings; pinned settings are read-only in the UI](0014-env-wins-runtime-settings.md)
- [ADR-0015: Pluggable reverse-proxy provider — built-in Caddy, or a Cloudflare Tunnel](0015-proxy-provider-abstraction.md)
- [ADR-0016: Stack backups — volume archives to pluggable remote storage](0016-stack-backups.md)
- [ADR-0017: Database-aware backups — Postgres dumps replace the data-volume snapshot; stops are scoped to the volumes being archived](0017-database-aware-dumps.md)
- [ADR-0018: Cron-based backup schedule with per-stack overrides, on the Elarion scheduler](0018-cron-backup-schedule.md)
- [ADR-0019: Backup quiesce — stops run per dependency level with a short grace, and `pause` is a second, crash-consistent mode](0019-pause-quiesce-and-parallel-stops.md)
- [ADR-0020: Per-service backup settings — labels win, UI overrides fill the gaps, and the plan preview shows the effective result](0020-backup-service-settings-labels-win-ui-fills-gaps.md)
- [ADR-0022: The reverse proxy runs in Watchtower's own process (YARP + ACME), and is the default](0022-in-process-yarp-proxy.md)
- [ADR-0023: Login hosts are Watchtower self-routes](0023-login-hosts-are-watchtower-self-routes.md)
