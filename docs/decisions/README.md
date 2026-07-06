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
