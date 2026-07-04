# ADR-0004: Singleton services access EF Core through `IServiceScopeFactory`

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0002](0002-sqlite-via-ef-core.md) (the EF Core adoption this reconciles with).

## Context

Several Watchtower services are **singletons** by necessity:

- `DeployQueueService` — an in-process, per-stack deploy queue that owns long-running worker tasks and must
  be a single instance so RPC handlers and the webhook endpoint enqueue onto the same queues.
- `SelfUpdateService`, `StackUpdateService`, and the two background checkers — `IHostedService`s /
  `BackgroundService`s, which are singletons.

EF Core's `DbContext` is **scoped** and not thread-safe: a singleton must not capture one. The pre-Elarion
code sidestepped this by opening a fresh raw `SqliteConnection` per call from its singleton repositories —
cheap, stateless, and safe. Moving to EF Core ([ADR-0002](0002-sqlite-via-ef-core.md)) removes that
per-call-connection model and needs a replacement that keeps singletons from holding a context.

## Decision

**Request-scoped handlers inject `WatchtowerDbContext` directly; singleton services open a short-lived
scope per unit of work via `IServiceScopeFactory`.**

- Handlers (resolved per JSON-RPC request) take `WatchtowerDbContext` as a constructor dependency and write
  LINQ inline — the idiomatic Elarion style.
- Singletons take `IServiceScopeFactory`. Each discrete database interaction does
  `using var scope = _scopeFactory.CreateScope();` and resolves `WatchtowerDbContext` (or the scoped
  `SettingsStore` / `RegistryAuthBuilder`) from it. The deploy engine's incremental writes (event status,
  captured output) each run in their own short scope — the EF equivalent of the old open-connection-per-call.

## Consequences

- **The old per-call-connection safety is preserved** under EF Core: no singleton ever holds a `DbContext`,
  and concurrent workers never share one.
- **A little ceremony in the singletons.** DB touches are wrapped in `CreateScope()` rather than being bare
  method calls; small private helpers keep the deploy engine readable.
- **Many short-lived scopes.** The self-update and deploy flows open a scope per operation. For the tiny
  data volumes and infrequent cadence of a control-plane tool this is negligible; it is not a hot path.

### Rejected alternatives

- **Inject a singleton `DbContext`.** Unsafe — `DbContext` is not thread-safe and not designed for
  singleton lifetime.
- **Register a pooled `IDbContextFactory` and use it everywhere, including handlers.** Works, but mixes two
  access patterns (factory vs. scoped injection) across the codebase; injecting the scoped context into
  handlers is the framework-idiomatic default, so scopes are confined to the few services that truly need them.
