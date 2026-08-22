# Watchtower

A self-hosted **Docker Compose GitOps deployer**. Register a stack (a git repository + a compose
file), and Watchtower clones it, pulls images, and runs `docker compose up -d` — on demand from the
UI, via an authenticated webhook from your CI, or **pull-based** for hosts your CI can't reach:
per stack, deploy as soon as polling finds a newer image or a new commit on the branch, or once per
day at a fixed time (e.g. 02:00). It also inspects running containers, streams logs and deploy
output live, checks registries for newer images, and can update itself. Stack volumes can be
**backed up on a daily schedule to external storage** (SFTP — e.g. a Hetzner Storage Box — or a
mounted directory) with retention and optional encryption, and any volume can be downloaded as an
archive straight from the UI — see [docs/backups.md](docs/backups.md), including the restore
procedure.

Watchtower is built on **[Elarion](https://github.com/swimmesberger/Elarion)** — an opinionated .NET
application framework for module-based handler pipelines with compile-time registration and JSON-RPC
hosting. Every operation is a `[Handler]` exposed over JSON-RPC; the React frontend calls a typed
client generated from the exported schema.

Watchtower also has a **built-in reverse proxy** for putting your stacks on the internet: point a
domain at a service under *Routes* and it is served with a certificate Watchtower obtains and renews
itself over ACME — in its own process, with no sibling proxy container to run. Publish `80:8081` and
`443:8443` and set `WATCHTOWER__PROXY__ENABLED=true`; ingress binds its own container ports, so an
unknown domain arriving there gets a 404 rather than Watchtower's own UI, which stays on 8080 for you
to bind privately. A **Cloudflare Tunnel** provider is there for
hosts that cannot open ports at all, and the older Caddy-container provider stays supported for
existing installations. See [docs/reverse-proxy/](docs/reverse-proxy/README.md) and
[ADR-0020](docs/decisions/0020-in-process-yarp-proxy.md).

> Authentication is **opt-in and off by default**, so an upgrade cannot lock you out. Left off,
> Watchtower is unauthenticated and belongs behind an authenticating reverse proxy (Cloudflare Access,
> Authelia, oauth2-proxy, …). Set `WATCHTOWER__AUTH__ENABLED=true` to use built-in local accounts
> instead: the first start creates an `admin` user from `WATCHTOWER__AUTH__BOOTSTRAPPASSWORD` (or logs a
> generated one), the UI gains a login page, and every handler and log stream requires a session.
> Any signed-in account can add **two-factor authentication** to itself from *Security* — a TOTP
> authenticator app plus ten single-use recovery codes.
> Enabling it also lets you protect **other proxied apps** centrally (per-app Public / Authenticated /
> Restricted access, with signed identity forwarded upstream) — see the operator guide,
> [docs/central-auth/README.md](docs/central-auth/README.md).
> `WATCHTOWER__AUTH__RESETPASSWORD` is the break-glass hook if you lock yourself out — it resets the
> `admin` password *and clears that account's second factor*, since a lost authenticator is the usual
> reason to need it.
> Either way, only the `/api/webhooks/*`, `/api/app/*` and `/api/mgmt/*` routes are designed to be
> reachable by unauthenticated external callers, and each is protected by a per-stack (or
> per-application) bearer token.

## Tech stack

- **Backend:** .NET 10 / ASP.NET Core, [Elarion](https://github.com/swimmesberger/Elarion) modules &
  handlers, JSON-RPC (`POST /rpc`), EF Core + **SQLite** (single-file, zero external dependencies).
- **Frontend:** React 19 + Vite, TanStack Router + Query, Tailwind v4, shadcn/ui. Talks to the backend
  through the generated `@swimmesberger/elarion-jsonrpc-client-generator` client.
- **Deployment:** a single Docker image bundling the .NET app, the Docker CLI + Compose plugin, and git.

## How it works

```
┌────────────┐   JSON-RPC (/rpc)    ┌───────────────────────────────┐   docker.sock   ┌────────────┐
│ React SPA  │ ───────────────────► │  Watchtower.Api (ASP.NET)     │ ──────────────► │  Docker    │
│ (wwwroot)  │   SSE (/api/.../…)   │  Elarion modules + handlers   │  git / compose  │  daemon    │
└────────────┘ ◄─────────────────── │  EF Core → SQLite (/data)     │ ──────────────► │  + stacks  │
                                    └───────────────────────────────┘                 └────────────┘
```

- **JSON-RPC** (`POST /rpc`) serves every CRUD/action operation — see the methods in
  [`rpc-schema.json`](rpc-schema.json) (`credentials.*`, `registries.*`, `stacks.*`, `containers.*`,
  `deployments.active`, `system.*`).
- **Plain HTTP** endpoints handle what JSON-RPC can't: the deploy webhook, two Server-Sent-Event
  streams (live deploy output + container logs), the App API, and `/health`.
- **App API** (`/api/app/*`): deployed applications can query **their own** deployment status,
  version, deploy history and logs. Watchtower injects a per-stack bearer token into every deploy as
  `WATCHTOWER_APP_TOKEN` (alongside `WATCHTOWER_STACK_ID` and, when configured, `WATCHTOWER_URL`),
  and each endpoint resolves the caller's containers server-side from its compose project — so a
  stack can only ever see itself, never another stack, deploy output, or credentials. See
  [docs/public-app-api.md](docs/public-app-api.md) and [ADR-0008](docs/decisions/0008-public-app-api.md).
  Its multi-tenant sibling, the **Management API** (`/api/mgmt/*`), lets a stack manage the tenants
  of one template it was explicitly granted — see [docs/public-mgmt-api.md](docs/public-mgmt-api.md) and
  [ADR-0009](docs/decisions/0009-public-management-api.md).
- **Deploy engine:** an in-process per-stack queue with coalescing — at most one deploy runs per stack,
  with one pending slot. A deploy clones the repo, builds a scoped `DOCKER_CONFIG`, writes a temp
  `.env` from the stack's variables, then `docker compose pull` + `up -d --remove-orphans`.
- **Self-update:** Watchtower can pull its own newer image and spawn a short-lived *coordinator*
  sibling container that recreates it via the Docker API — cloning the running container's
  configuration onto the new image, with automatic rollback if the replacement fails to start
  (a container can't restart itself). Needs no configuration beyond the Docker socket.

See [docs/architecture.md](docs/architecture.md) for the module/handler layout,
[docs/elarion.md](docs/elarion.md) for how the project consumes the framework,
[docs/reverse-proxy/](docs/reverse-proxy/README.md) for exposing stacks on public domains (the
built-in in-process proxy, or a Cloudflare Tunnel),
[docs/scaling-beyond-one-node.md](docs/scaling-beyond-one-node.md) for what to run when a single host
is no longer enough (Docker Swarm vs k3s),
[docs/host-metrics.md](docs/host-metrics.md) for enabling the Dashboard's host CPU/RAM/disk strip,
[docs/metrics-history.md](docs/metrics-history.md) for the metrics backends (persisted SQLite history by default, BYO InfluxDB opt-in),
[docs/backups.md](docs/backups.md) for scheduled stack backups — setup, encryption, and **how to restore** — and
[docs/decisions/](docs/decisions/) for the architecture decision records (ADRs).

## Project structure

```
Watchtower/
├── src/
│   ├── Watchtower.Application/   # Elarion modules + handlers, EF entities/DbContext, service layer
│   │   ├── Entities/             #   EF entities (Credential, Registry, Stack, DeployEvent, …)
│   │   ├── Persistence/          #   WatchtowerDbContext ([GenerateDbSets]) + migrations
│   │   ├── Services/             #   Docker/compose/git clients, the deploy engine, self/stack update
│   │   └── Modules/              #   one folder per module: Credentials, Registries, Stacks, …
│   ├── Watchtower.Api/           # ASP.NET host: Program.cs, coordinator mode, webhook + SSE endpoints
│   ├── Watchtower.AppHost/       # .NET Aspire orchestration (runs the API + web together in dev)
│   └── watchtower-web/           # React SPA (generated RPC client in src/generated/)
├── deploy/docker/                # Dockerfile + example docker-compose.yml
├── rpc-schema.json               # exported JSON-RPC schema (source for the frontend client generator)
└── Watchtower.slnx
```

## Development

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/), [Node.js 22+](https://nodejs.org/), and
Docker (the daemon must be reachable at `/var/run/docker.sock` for container/deploy features).

**Run everything with .NET Aspire (recommended).** The `Watchtower.AppHost` runs the API and the web
frontend as one unit and opens a dashboard with logs, traces, and endpoints. It injects the API URL into
the frontend as `VITE_API_URL`, so there's no separate frontend/backend wiring to manage.

```bash
(cd src/watchtower-web && npm install)   # once
dotnet run --project src/Watchtower.AppHost
```

**Or run them separately:**

```bash
dotnet run --project src/Watchtower.Api                 # API on http://localhost:5080
# in another terminal — Vite proxies /rpc, /api, /health to the API:
(cd src/watchtower-web && npm install && npm run dev)   # http://localhost:5173
```

The frontend's typed RPC client is generated from `rpc-schema.json` on every build (`prebuild` →
`generate:rpc`). After changing a handler's request/response types, regenerate the schema — pass an
**absolute path** to the repo-root file:

```bash
dotnet run --project src/Watchtower.Api -- --export-schema "$PWD/rpc-schema.json"
```

A bare relative path (`rpc-schema.json`) would land in `src/Watchtower.Api/` instead: `dotnet run
--project` runs the app with its working directory set to the project folder, not where you invoked it.

## Deployment

Build/pull the image and run it with the Docker socket mounted — see
[`deploy/docker/docker-compose.yml`](deploy/docker/docker-compose.yml). CI publishes
[`swimmes/watchtower:latest`](https://hub.docker.com/r/swimmes/watchtower) on every push to `main`.

### Configuration

Bind via the `Watchtower` config section or `WATCHTOWER__*` environment variables. Most settings are
also editable at runtime under **Settings** in the UI (persisted in the database, applied without a
restart — `Auth:Enabled` excepted, which applies on the next start). **Environment variables always
win** over runtime-edited settings ([ADR-0014](docs/decisions/0014-env-wins-runtime-settings.md)):
a setting supplied via env var shows as pinned (read-only) in the UI, and removing the variable makes
it editable again. In particular, `WATCHTOWER__AUTH__ENABLED=false` + restart always disables
authentication, whatever was configured in the UI.

| Key | Env | Default | Purpose |
| --- | --- | --- | --- |
| `DbPath` | `WATCHTOWER__DBPATH` | `/data/watchtower.db` | SQLite database file path. |
| `DockerApiVersion` | `WATCHTOWER__DOCKERAPIVERSION` | `1.43` | Docker Engine API version used for direct calls and `docker compose`. |
| `PublicBaseUrl` | `WATCHTOWER__PUBLICBASEURL` | *(unset)* | Publicly reachable base URL; injected into every deploy as `WATCHTOWER_URL` — straight into the containers, no compose changes needed — for the [App API](docs/public-app-api.md). |
| `AutoCheckEnabled` | `WATCHTOWER__AUTOCHECKENABLED` | `false` | Periodically check for a newer Watchtower image. |
| `StackCheckEnabled` | `WATCHTOWER__STACKCHECKENABLED` | `false` | Periodically check stacks for newer images. |
| `ImagePruneEnabled` | `WATCHTOWER__IMAGEPRUNEENABLED` | `false` | Periodically remove dangling (untagged) images — `docker image prune -f`, never `-a`. Interval via `WATCHTOWER__IMAGEPRUNEINTERVALMINUTES` (default `1440`). |
| `Metrics:Backend` | `WATCHTOWER__METRICS__BACKEND` | `sqlite` | Metrics source: `sqlite` (persisted history), `memory` (live only), or `influxdb` (read an external store). Runtime-switchable under Settings → Metrics — see [docs/metrics-history.md](docs/metrics-history.md). |
| `Metrics:RetentionDays` | `WATCHTOWER__METRICS__RETENTIONDAYS` | `30` | History window of the `sqlite` backend (1–365 days). |

`WATCHTOWER_DOCKER_CONFIG` / `DOCKER_CONFIG` point at a mounted host `config.json` for private pulls.

`WATCHTOWER_HOST_PROC` (e.g. `/host/proc`) enables the Dashboard's host CPU/RAM/load strip by pointing at
a read-only `/proc` mount; `WATCHTOWER_HOST_ROOTFS` (optional) points at a host-root mount for true disk
usage (else disk falls back to Docker's `df`). Both are opt-in — see
[docs/host-metrics.md](docs/host-metrics.md). Container and per-stack metrics need neither.

## License

Licensed under the [Apache License 2.0](LICENSE). Copyright © 2026 Simon Wimmesberger.
