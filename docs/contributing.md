# Contributing

## Prerequisites

- .NET SDK per [`global.json`](../global.json)
- Node 22 (frontend)
- A container engine — Docker Desktop, colima, or Podman. Needed for the tests, which run against a
  real PostgreSQL ([ADR-0024](decisions/0024-postgresql-only-and-state-in-the-database.md)).

## Running the tests

```bash
dotnet test Watchtower.slnx
```

Both suites need PostgreSQL. By default they start one per test assembly with Testcontainers, over
whatever `DOCKER_HOST` — or `/var/run/docker.sock` — points at. Each test host then gets its own
database, cloned from a template the migrations are applied to once per run, and dropped on dispose.
Nothing is left behind: the container stops when the last test finishes.

### Podman

Testcontainers speaks the Docker API, and Podman serves it. Point it at the podman socket once:

```bash
podman machine start
export DOCKER_HOST="unix://$(podman machine inspect --format '{{.ConnectionInfo.PodmanSocket.Path}}')"
```

A `/var/run/docker.sock` symlinked to that path works too, and is what a Docker-compatible setup on
macOS usually already has.

### Bringing your own PostgreSQL

If Testcontainers cannot reach your engine — or you would rather not pay the container start on every
run — set `WATCHTOWER_TEST_PG` to a server the tests may `CREATE DATABASE` on, and they will use it
instead. This is also what CI does, against its `services: postgres` container.

```bash
podman run -d --name wtpg -e POSTGRES_PASSWORD=wt -p 15432:5432 postgres:18-alpine
export WATCHTOWER_TEST_PG="Host=127.0.0.1;Port=15432;Database=postgres;Username=postgres;Password=wt"
dotnet test Watchtower.slnx
```

The account needs `CREATEDB`, nothing more. The tests never touch the database named in the connection
string beyond connecting to it; they create and drop their own (`wt_template_*`, `wt_test_*`).

## Running the app

```bash
cd src/watchtower-web && npm install   # once
dotnet run --project src/Watchtower.AppHost
```

The Aspire AppHost starts PostgreSQL (with a data volume, so it survives restarts), the API and the
Vite dev server together, and opens the dashboard.

To run the API on its own, give it a connection string:

```bash
export WATCHTOWER__DATABASE__CONNECTIONSTRING="Host=127.0.0.1;Port=15432;Database=watchtower;Username=postgres;Password=wt"
dotnet run --project src/Watchtower.Api
```

There is no default and no file fallback — see [upgrading.md](upgrading.md) if you are coming from a
build that stored everything in `/data/watchtower.db`.

## Migrations

The schema is one migration, `InitialPostgreSql`, regenerated from the model by ADR-0024. Add to it the
usual way:

```bash
dotnet ef migrations add <Name> \
  --project src/Watchtower.Application --startup-project src/Watchtower.Api \
  --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes \
  --project src/Watchtower.Application --startup-project src/Watchtower.Api
```

The design-time factory uses a placeholder connection string, so scaffolding needs no database.
`dotnet ef database update` does — set `WATCHTOWER__DATABASE__CONNECTIONSTRING` first, or the CLI will
have nothing to connect to. (The application migrates itself on startup, so this is rarely needed.)

Two expression indexes (`lower(compose_project_name)`, `lower(owner), lower(name)`) are raw SQL at the
end of the initial migration's `Up`, because EF cannot model them. If either column moves, move the
statement with it — the entity configurations carry a comment pointing here.

## The RPC schema

`rpc-schema.json` is generated and committed; CI fails if it is stale. Regenerate **from the repo
root**, with an absolute path — `dotnet run --project` sets the working directory to the project:

```bash
dotnet run --project src/Watchtower.Api -- --export-schema "$PWD/rpc-schema.json"
cd src/watchtower-web && npm run generate:rpc && npm run typecheck
```
