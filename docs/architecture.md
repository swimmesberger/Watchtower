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
unit of work (the pre-Elarion code opened a raw SQLite connection per call; this is the EF equivalent).
Settings (`app_settings`) are accessed through the scoped `SettingsStore`.

## Self-update

A running container cannot `docker compose up -d` itself — Docker would kill the process mid-run. So
Watchtower pulls the new image, then spawns a **coordinator** sibling container (same image, same socket)
launched with `--self-update`; it waits ~3 s for the original request to return, runs `compose up -d` to
recreate Watchtower, and exits. On the next startup Watchtower reconciles the coordinator's exit code to
report success/failure. See `Services/SelfUpdateService.cs` and `Watchtower.Api/CoordinatorMode.cs`.

## Persistence

SQLite via EF Core (`Microsoft.EntityFrameworkCore.Sqlite`), snake_case columns, WAL enabled at startup.
The schema is created by the `InitialCreate` migration (applied on startup via `MigrateAsync`), and any
deploys left `running`/`queued` by a crash are reset to `failed`. Entities keep integer identity keys to
preserve the API contract.
