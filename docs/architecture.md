# Architecture

Two backend projects, a dev-only Aspire orchestrator, and the SPA:

- **`Watchtower.Application`** — the Elarion app library (`[assembly: UseElarion]`). Holds the modules
  and handlers, the EF entities + `WatchtowerDbContext`, and the service layer. Referenced by the host.
- **`Watchtower.Api`** — the ASP.NET host (`[assembly: GenerateModuleBootstrapper]`). Wires transport,
  the database, the plain HTTP endpoints, and process-entry concerns (coordinator + schema export).
- **`Watchtower.AppHost`** — a .NET Aspire host that runs the API + the Vite web app together in
  development (`AddViteApp`, injecting the API URL as `VITE_API_URL`). Not part of the shipped image.
- **`watchtower-web`** — the React SPA, served from `wwwroot/` in production. Its API base comes from
  `VITE_API_URL` (absolute, under Aspire) or is empty (same-origin: production wwwroot, or the Vite proxy
  during a standalone `npm run dev`).

## Modules & the API surface

Each module is a namespace under `Modules/` with an `[AppModule]` marker, a `JsonSerializerContext`, and
handlers. A handler is a use case, a DI registration, and a JSON-RPC method at once.

| Module | Methods |
| --- | --- |
| Credentials | `credentials.list` · `.create` · `.update` · `.delete` |
| Registries | `registries.list` · `.create` · `.update` · `.delete` · `.test` |
| Stacks | `stacks.list` · `.get` · `.create` · `.update` · `.delete` · `.deploy` · `.events` · `.getEnv` · `.setEnv` · `.checkUpdates` |
| Deployments | `deployments.active` |
| Containers | `containers.list` · `.restart` · `.stop` · `.remove` |
| System | `system.getSelf` · `.updateConfig` · `.check` · `.applyUpdate` · `.dockerConfig` |

Streaming and externally-facing operations stay as plain HTTP (`Watchtower.Api/Endpoints`):

- `POST /api/webhooks/stacks/{id}/deploy` — bearer-token deploy trigger (returns 404 when the stack's
  webhook is disabled, so it never reveals stack existence).
- `GET  /api/stacks/events/{eventId}/stream` — SSE: live deploy output, replaying stored output after completion.
- `GET  /api/containers/{id}/logs` — SSE: container logs (demuxes Docker's framed log stream).
- `GET  /health`.

## The service layer

`Services/` carries the Docker/compose/git machinery, unchanged in behaviour from the pre-Elarion codebase:

- **`DockerEngineClient`** — talks to the Docker Engine API over the Unix socket (list/inspect/pull,
  remote manifest digests for update checks, container create/start/wait for the self-update coordinator).
- **`ComposeCliService`** / **`GitCloneService`** — subprocess wrappers around `docker compose` and `git`.
- **`RegistryAuthBuilder`** — builds a scoped `DOCKER_CONFIG` merging host credentials with the
  configured registry credentials.
- **`DeployQueueService`** — the per-stack deploy queue with coalescing (one running + one pending slot).
- **`DeployOutputBroadcaster`** — fans deploy output out to SSE subscribers in real time.
- **`SelfUpdateService`** / **`StackUpdateService`** (+ their background schedulers) — update checks
  (registry image digests + git branch head vs. last deployed commit) and the self-update lifecycle.
- **`AutoDeployBackgroundService`** — pull-based deployment for hosts an inbound webhook can't reach.
  Per-stack opt-in (`Stack.AutoDeployMode`): `OnChange` redeploys as soon as a poll (on the stack
  check interval) finds a newer image or commit; `Scheduled` checks once per day at
  `Stack.AutoDeployTime` (server-local) and deploys only when something new is available. Deploys are
  enqueued through `DeployQueueService` (`triggered by auto-update` / `schedule`).

### Scoping model

Handlers are request-scoped and inject `WatchtowerDbContext` directly. The **singletons** —
`DeployQueueService`, `SelfUpdateService`, `StackUpdateService`, and the background services — must not
capture a scoped `DbContext`, so they open short-lived scopes through `IServiceScopeFactory` for each
unit of work (the pre-Elarion code opened a raw database connection per call; this is the EF equivalent).
Settings (`app_settings`) are accessed through the scoped `SettingsStore`.

## Self-update

A running container cannot recreate itself — the process dies the moment its container stops. So
Watchtower pulls the new image, then spawns a **coordinator** sibling container (the just-pulled image,
same socket) launched with `--self-update`. The coordinator recreates Watchtower purely via the Docker
API: it clones the running container's configuration onto the new image (`ContainerCloneSpec` — carries
Config/HostConfig/networks, drops the id-derived default hostname and stale runtime fields), waits ~3 s
for the original request to return, stops and renames the old container aside, creates and starts the
replacement under the original name, and rolls back to the old container if that fails. No compose file
is read or required, so self-update needs zero configuration and works for any deployment shape; the
flip side is that a compose file edited since the last `docker compose up -d` is not re-asserted by a
self-update — run compose on the host to apply compose-file changes. The main process watches the
coordinator, so a failed (rolled-back) run surfaces immediately; when the update succeeds, the next
startup reconciles the coordinator's exit code instead. See `Services/SelfUpdateService.cs`,
`Services/ContainerCloneSpec.cs` and `Watchtower.Api/CoordinatorMode.cs`.

## Persistence

PostgreSQL via EF Core (`Npgsql.EntityFrameworkCore.PostgreSQL`), snake_case columns
([ADR-0024](decisions/0024-postgresql-only-and-state-in-the-database.md)). One `NpgsqlDataSource`
singleton is shared by the context and, later, by Elarion's PostgreSQL packages. The connection string
comes from `Watchtower:Database:ConnectionString`, falling back to `ConnectionStrings:watchtower` so
Aspire's `WithReference` works; there is no default, and a missing one is a startup error.

The schema is created by the `InitialPostgreSql` migration (applied on startup via `MigrateAsync`), and
any deploys left `running`/`queued` by a crash are reset to `failed`. Entities keep integer identity keys
to preserve the API contract. Rows several writers can meet on — realms, stacks, routes, groups — carry
PostgreSQL's `xmin` as their EF concurrency token, and a lost race becomes a `Conflict` result through
`ConcurrencyConflictDecorator` rather than an unhandled exception; users carry Identity's own
`ConcurrencyStamp` instead, because the user store reads detached and writes back.

Watchtower stored everything in a SQLite file before ADR-0024. The one-shot importer that carried it
across (automatically on first start, or via `--import-sqlite <path>`) was removed on 2026-08-25 once
every installation had migrated — see [upgrading.md](upgrading.md). No SQLite code remains.
