# ADR-0005: Development orchestration with a .NET Aspire AppHost

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0006](0006-frontend-notargets-project.md) (surfacing the web app in the solution).

## Context

Watchtower is a .NET backend plus a Vite + React SPA. In development these were two independent processes
started by hand — `dotnet run` for the API and `npm run dev` for the SPA — with the frontend reaching the
API through a hard-coded Vite proxy target. There was no single "run the app" entry point, and the proxy's
fixed port is fragile.

The [Elarion sample](https://github.com/swimmesberger/Elarion/tree/main/samples/Billing) and Swerp both
orchestrate their frontend + backend with a **.NET Aspire AppHost** (`AddViteApp`, injecting the API URL
into the frontend). Adopting the same removes the split into one runnable unit.

## Decision

**Add a `Watchtower.AppHost` (Aspire) that runs the API and the web app together and injects the API URL
into the frontend.**

- `AppHost.cs` does `AddProject<Projects.Watchtower_Api>("api")` and
  `AddViteApp("web", "../watchtower-web").WithReference(api).WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))`.
  `dotnet run --project src/Watchtower.AppHost` starts both and opens the Aspire dashboard.
- Because Watchtower persists to SQLite ([ADR-0002](0002-sqlite-via-ef-core.md)), **no database container
  is provisioned** — unlike the Postgres-backed Elarion/Swerp AppHosts.
- The frontend derives its API base from `VITE_API_URL`: absolute (cross-origin) under Aspire, and empty
  (same-origin) otherwise — production `wwwroot`, or a standalone `npm run dev` via the Vite proxy. The API
  adds a permissive **development-only** CORS policy so the cross-origin dev server can reach `/rpc`,
  `/api/*`, and the SSE streams.
- The AppHost is a **dev-time tool only** — it is not referenced by, or shipped in, the Docker image.

## Consequences

- **One command runs the whole app** with a dashboard for logs and endpoints, and no per-service wiring to
  remember. The frontend/backend "split" is gone from the developer's point of view.
- **Consistency with Swerp and the Elarion sample** — the orchestration pattern is identical (minus the
  database resource).
- **A dev-only dependency is added** (Aspire hosting packages) and running the AppHost needs the Aspire
  prerequisites. Neither affects the shipped image or a plain `dotnet run` of the API.

### Rejected alternatives

- **Keep two terminals + a hard-coded proxy port.** No single entry point, and the fixed proxy target
  breaks under Aspire's dynamically-assigned ports.
- **A docker-compose dev stack for the two processes.** Heavier than Aspire for a two-process dev loop and
  gives no dashboard or service-discovery injection.
