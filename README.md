# Watchtower

A self-hosted **Docker Compose GitOps deployer**. Register a stack (a git repository + a compose
file), and Watchtower clones it, pulls images, and runs `docker compose up -d` — on demand from the
UI, or via an authenticated webhook from your CI. It also inspects running containers, streams logs
and deploy output live, checks registries for newer images, and can update itself.

Watchtower is built on **[Elarion](https://github.com/swimmesberger/Elarion)** — an opinionated .NET
application framework for module-based handler pipelines with compile-time registration and JSON-RPC
hosting. Every operation is a `[Handler]` exposed over JSON-RPC; the React frontend calls a typed
client generated from the exported schema.

> Watchtower has **no built-in authentication**. Put it behind an authenticating reverse proxy
> (Cloudflare Access, Authelia, oauth2-proxy, …). Only the `/api/webhooks/*` routes are designed to
> be reachable by unauthenticated external callers, and each is protected by a per-stack bearer token.

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

- **JSON-RPC** (`POST /rpc`) serves every CRUD/action operation — see the 29 methods in
  [`rpc-schema.json`](rpc-schema.json) (`credentials.*`, `registries.*`, `stacks.*`, `containers.*`,
  `deployments.active`, `system.*`).
- **Plain HTTP** endpoints handle what JSON-RPC can't: the deploy webhook and two Server-Sent-Event
  streams (live deploy output + container logs), plus `/health`.
- **Deploy engine:** an in-process per-stack queue with coalescing — at most one deploy runs per stack,
  with one pending slot. A deploy clones the repo, builds a scoped `DOCKER_CONFIG`, writes a temp
  `.env` from the stack's variables, then `docker compose pull` + `up -d --remove-orphans`.
- **Self-update:** Watchtower can pull its own newer image and spawn a short-lived *coordinator*
  sibling container that runs `docker compose up -d` to recreate it (a container can't restart itself).

See [docs/architecture.md](docs/architecture.md) for the module/handler layout,
[docs/elarion.md](docs/elarion.md) for how the project consumes the framework, and
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
`generate:rpc`). After changing a handler's request/response types, regenerate the schema:

```bash
dotnet run --project src/Watchtower.Api -- --export-schema rpc-schema.json
```

## Deployment

Build/pull the image and run it with the Docker socket mounted — see
[`deploy/docker/docker-compose.yml`](deploy/docker/docker-compose.yml). CI publishes
`ghcr.io/swimmesberger/watchtower:latest` on every push to `main`.

### Configuration

Bind via the `Watchtower` config section or `WATCHTOWER__*` environment variables:

| Key | Env | Default | Purpose |
| --- | --- | --- | --- |
| `DbPath` | `WATCHTOWER__DBPATH` | `/data/watchtower.db` | SQLite database file path. |
| `DockerApiVersion` | `WATCHTOWER__DOCKERAPIVERSION` | `1.43` | Docker Engine API version used for direct calls and `docker compose`. |
| `AutoCheckEnabled` | `WATCHTOWER__AUTOCHECKENABLED` | `false` | Periodically check for a newer Watchtower image. |
| `StackCheckEnabled` | `WATCHTOWER__STACKCHECKENABLED` | `false` | Periodically check stacks for newer images. |

`WATCHTOWER_DOCKER_CONFIG` / `DOCKER_CONFIG` point at a mounted host `config.json` for private pulls.

## License

Licensed under the [Apache License 2.0](LICENSE). Copyright © 2026 Simon Wimmesberger.
