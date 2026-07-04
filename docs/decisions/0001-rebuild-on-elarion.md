# ADR-0001: Rebuild Watchtower on the Elarion framework

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0002](0002-sqlite-via-ef-core.md) (the persistence change this forces),
  [ADR-0003](0003-jsonrpc-primary-transport.md) (the transport it introduces).

## Context

Watchtower — a self-hosted Docker Compose GitOps deployer (stacks, registries, credentials, webhooks,
container management, self-update) — lived inside the [Swerp](https://github.com/swimmesberger/Swerp)
monorepo as a bespoke app: ASP.NET Core Minimal-API REST endpoints, a hand-rolled SQLite data-access
layer over raw ADO.NET, and NativeAOT publishing. It shared nothing with Swerp except the repository.

Swerp itself is built on **[Elarion](https://github.com/swimmesberger/Elarion)** — a .NET application
framework for module-based handler pipelines, compile-time registration, and JSON-RPC hosting. Watchtower
is a distinct product with its own release cadence, its own deployment target, and no code coupling to
Swerp. Keeping it in the monorepo meant its CI, its dependency graph, and its lifecycle were entangled
with an unrelated application; and its bespoke REST/ADO stack duplicated concerns Elarion already solves
(registration, dispatch, serialization, schema export, a typed client).

## Decision

**Extract Watchtower into its own repository and rebuild every operation as an Elarion handler.**

- Watchtower moves to a standalone repo (`github.com/swimmesberger/Watchtower`) with its own solution,
  CI, and Docker image — removed from the Swerp monorepo.
- Each use case becomes a `sealed` class annotated with `[Handler("module.operation")]` implementing
  `IHandler<TRequest, Result<TResponse>>`, grouped into `[AppModule]`s (`Credentials`, `Registries`,
  `Stacks`, `Deployments`, `Containers`, `System`). The Elarion source generators emit the registration
  and transport wiring; there is no hand-maintained endpoint list.
- Failures are transport-neutral `AppError`s (`NotFound`, `Validation`, …) mapped to JSON-RPC error codes
  by the framework.
- Elarion is consumed as published NuGet packages (pinned via `ElarionVersion`), not vendored source.

## Consequences

- **Watchtower and Swerp now share one mental model.** Modules, handlers, `Result<T>`, source-generated
  registration, and the JSON-RPC schema/client work identically in both, so knowledge transfers and the
  framework's future capabilities (e.g. MCP tools) come essentially for free.
- **Independent lifecycle.** Its own repo means its own CI, versioning, issues, and image — changes to
  Swerp no longer touch Watchtower and vice versa.
- **New runtime dependency.** Watchtower now depends on the Elarion packages and their transitive graph,
  where before it had almost none. For a self-hosted single-container tool this is an acceptable trade for
  the removed hand-rolled infrastructure.
- **It forces two follow-on decisions.** Adopting Elarion's EF Core generators drives the persistence and
  AOT change ([ADR-0002](0002-sqlite-via-ef-core.md)); adopting JSON-RPC as the handler transport drives
  how streaming and webhooks are handled ([ADR-0003](0003-jsonrpc-primary-transport.md)).

### Rejected alternatives

- **Keep the bespoke REST + ADO stack, extract as-is.** Achieves the repo split but keeps two divergent
  stacks to maintain and forgoes the source-generated registration, schema export, and typed client.
- **Vendor Elarion source into the repo.** Rejected for the same reason Swerp consumes it as packages —
  vendoring couples Watchtower to a framework snapshot and loses clean upgrades.
