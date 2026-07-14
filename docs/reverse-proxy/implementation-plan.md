# Reverse Proxy + Multi-Tenant Routing — Implementation Plan

> Status: proposal / design. Nothing here is implemented yet.
> Branch/worktree: `worktree-reverse-proxy-plan`.
> Grounded against the current code (Stacks module, deploy pipeline, host wiring, web modules, deploy
> manifest) and Elarion `0.2.3-preview.79.1`.

## 1. Goal

Give Watchtower a built-in reverse proxy so that a non-expert operator can point a **domain** at a
**service** and get automatic HTTPS, with every deployed service staying internal to Docker and only
the proxy exposed publicly. Extend that to **single-node multi-tenancy**: run the same stack many
times (one isolated copy per tenant), each bound to its own subdomain, plus optional **customer-owned
custom domains**.

The UX bar: the operator's mental model is a table of `domain → service`. Everything underneath
(networks, certificates, config reloads) is invisible.

## 2. Design decisions (and why)

These refine the earlier chat design after reading the actual code. Two decisions changed on grounding
— called out below.

### 2.1 Proxy engine: **Caddy**
Automatic HTTPS with near-zero config, human-readable config, a first-class **admin API** for
zero-downtime reloads, and on-demand TLS available if we ever need it. The image ships as `caddy:2`.

### 2.2 Source of truth: **Watchtower's DB owns the routing table; Caddy config is generated from it**
A new `Route` table is authoritative. On any change Watchtower regenerates the Caddy config and reloads.
This is what makes the UX simple (the UI is CRUD over a table) and keeps user compose files untouched.
We do **not** scatter proxy labels onto user containers.

### 2.3 **CHANGED ON GROUNDING** — Explicit Caddy site blocks for *every* known domain; on-demand TLS is optional, not core
Earlier we leaned on Caddy on-demand TLS + an `ask` endpoint as the central mechanism for custom
domains. Now that we've confirmed Watchtower owns the full domain list in its DB and admin-API reloads
are cheap, the simpler and more predictable design is to **generate one explicit site block per
registered domain** (managed subdomains *and* custom domains alike) and reload on change. Explicit
blocks are inherently self-limiting (Caddy only ever tries to issue certs for domains we wrote), so the
abuse/rate-limit concern that motivated the `ask` gate mostly evaporates.

On-demand TLS + the `ask` endpoint is retained as an **optional enhancement** for a future white-label
scenario at scale (a tenant attaches many domains, or we want issuance without a reload). It is
specified in §9 but is **not** required for Phase 1.

### 2.4 **CHANGED ON GROUNDING** — Attach services to the edge network via the Docker API (connect-with-alias), not a compose override
Two ways to get a deployed service onto the shared proxy network:

- **Compose override file** (add an external `networks:` block via a second `-f`). Rejected as the
  primary mechanism: network list-merge semantics across compose files are base-topology-dependent
  (a service with no explicit `networks:` loses its default network the moment an override adds one),
  and, worse, **service names collide across tenants** — ten tenants each expose a service called
  `web`, so a shared network can't give Caddy an unambiguous upstream name.
- **Docker API connect-with-alias** (chosen). After `compose up`, Watchtower finds the routed
  service's container (filter by the compose project + service labels) and connects it to the proxy
  network with a **unique network alias** (`{composeProjectName}-{serviceName}`). Caddy's upstream is
  then that alias. This is topology-agnostic, gives us collision-free stable DNS names, and needs no
  edits to the user's compose. The cost is a small amount of new `DockerEngineClient` plumbing
  (network create/connect) and a reconnect step in the deploy pipeline (which already re-runs on every
  deploy/recreate).

### 2.5 Network topology: **separate control plane from ingress**
- `watchtower-control` — Caddy + Watchtower only. Caddy's admin API (`:2019`) and the optional `ask`
  endpoint live here, off the public ingress path and unreachable by tenant containers.
- Ingress — Phase 1: one shared `watchtower-edge` (Caddy + each routed service). Phase 2 hardening:
  **one ingress network per tenant/stack** (`watchtower-ingress-{stackId}`), each shared only between
  Caddy and that stack, so tenants can't reach each other even at L2. Caddy joins many networks; this
  is the clean isolation story for multi-tenant.

### 2.6 Watchtower fully manages the Caddy container (no operator compose changes required)
Watchtower creates and supervises the Caddy container itself over the Docker socket — the same
create/start/inspect/wait/remove machinery the self-update **coordinator** already uses
(`SelfUpdateService.PullAndSpawnAsync`, `SelfUpdateService.cs:248-299`), but long-lived (restart
policy `unless-stopped`, no `AutoRemove`). Caddy publishes `80`/`443` on the host and mounts two named
volumes (`caddy_data` for ACME state/certs, `caddy_config` for autosaved config). Binding a
not-yet-existing named volume auto-creates it, so no explicit volume-create call is needed.

Consequence: enabling the feature "just works" — the operator does not edit their compose. The only
host requirement is that ports 80/443 are free. (We still document an opt-out / a compose-managed Caddy
alternative for operators who want to own it — see §8.)

### 2.7 Staging
- **Phase 1 — core reverse proxy.** `Route` entity, Caddy lifecycle + config generation, edge network
  + connect-on-deploy, Proxy module + handlers, Proxy UI, deploy-manifest docs. Delivers domain→service
  with automatic HTTPS for existing stacks (manual multi-tenant works: create N stacks by hand, add N
  routes).
- **Phase 2 — templated multi-tenancy.** `StackTemplate` + tenant instances (Stacks linked to a
  template), one-click add-tenant, domain pattern, deploy-all, per-tenant ingress networks, optional
  custom-domain + on-demand TLS.

## 3. Data model

Conventions mirror the Stacks module exactly (`Entities/*.cs` + `[EntityConfiguration]` classes in
`Persistence/Configurations/WatchtowerEntityConfigurations.cs`; `int Id` identity keys; snake_case
tables; enums persisted via `HasConversion<string>()`; `DbSet`s appear automatically from
`[GenerateDbSets]` — **do not edit `WatchtowerDbContext.cs`**). After adding entities, generate an EF
migration with the design-time factory.

### 3.1 Phase 1 — `Route`

`src/Watchtower.Application/Entities/Route.cs`:

```csharp
public enum RouteStatus { Pending, AwaitingDns, Active, Error }
public enum DomainKind  { Managed, Custom }   // managed subdomain vs customer-owned domain

public sealed class Route {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }

    public required string Domain { get; set; }        // unique
    public required string ServiceName { get; set; }   // compose service to target
    public int ContainerPort { get; set; }
    public bool TlsEnabled { get; set; } = true;
    public bool IsPrimary { get; set; }                // canonical domain for the stack
    public DomainKind Kind { get; set; } = DomainKind.Managed;

    public RouteStatus Status { get; set; } = RouteStatus.Pending;
    public string? StatusDetail { get; set; }
    public DateTimeOffset? CertNotAfter { get; set; }  // populated by reconcile from Caddy
    public DateTimeOffset CreatedAt { get; set; }
}
```

`RouteConfiguration : IEntityTypeConfiguration<Route>` (add to
`WatchtowerEntityConfigurations.cs`, mirroring `StackConfiguration`):
- `ToTable("routes")`, `HasKey(x => x.Id)`.
- `HasIndex(x => x.Domain).IsUnique()`.
- `Property(x => x.Status).HasConversion<string>()`, `Property(x => x.Kind).HasConversion<string>()`.
- `HasOne(x => x.Stack).WithMany().HasForeignKey(x => x.StackId).OnDelete(DeleteBehavior.Cascade)`
  (and add `ICollection<Route> Routes` to `Stack` if we want the back-nav; optional).

### 3.2 Phase 2 — `StackTemplate` (+ `Stack` additions)

`src/Watchtower.Application/Entities/StackTemplate.cs`:

```csharp
public sealed class StackTemplate {
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string RepositoryUrl { get; set; }
    public required string ComposeFilePath { get; set; }
    public required string Branch { get; set; }
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }

    public required string DomainPattern { get; set; }   // e.g. "{tenant}.example.com"
    public required string TargetServiceName { get; set; }
    public int TargetPort { get; set; }

    public ICollection<StackTemplateEnvVar> BaseEnvVars { get; set; } = [];
    public ICollection<Stack> Instances { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
```

`Stack` gains: `int? TemplateId`, `StackTemplate? Template`, `string? TenantSlug`
(unique per template — `HasIndex(x => new { x.TemplateId, x.TenantSlug }).IsUnique()`).

**Instance = a `Stack`.** A tenant is a normal `Stack` row that references a template and carries a
`TenantSlug`. This is deliberately low-churn: the deploy pipeline already keys on `stackId` and gives
each stack full isolation via `ComposeProjectName` (Compose namespaces containers, the default network,
and named volumes by project). We reuse all of it. Creating a tenant **copies** the template's
repo/compose/branch/cred into the new Stack (keeping Stack self-contained so the deploy pipeline is
unchanged), sets `ComposeProjectName = slug`, merges `BaseEnvVars` + per-tenant overrides into
`StackEnvVars`, creates the managed `Route`, and enqueues a deploy. "Sync from template" re-copies
changed template fields to instances; "deploy all" fans out `DeployQueueService.Enqueue` per instance.

## 4. Backend — `Proxy` module + handlers

New module under `src/Watchtower.Application/Modules/Proxy/`, mirroring `Modules/Stacks/` one-for-one:

- `ProxyModule.cs` — `[AppModule("Proxy")] public static partial class ProxyModule { public static
  IJsonTypeInfoResolver GetJsonTypeInfoResolver() => ProxyJsonContext.Default; }`
- `ProxyContracts.cs` — `RouteDto`, input records, a `static RouteMapping` (in-memory mappers).
- `ProxyJsonContext.cs` — `[JsonSourceGenerationOptions(CamelCase, WhenWritingNull,
  UseStringEnumConverter)]` + one `[JsonSerializable]` per shared DTO and per handler nested
  `Query`/`Command`+`Response` with an explicit `TypeInfoPropertyName` (names collide otherwise).
- `Handlers/*.cs` — one file per operation, `[Handler("proxy.*")] sealed class :
  IHandler<.Query|Command, Result<.Response>>`, primary-ctor injecting `WatchtowerDbContext db`
  (+ `CaddyManager` where needed), in-handler validation returning `AppError.Validation`/`.NotFound`.
  **No `[HttpEndpoint]`, no DataAnnotations, no `[Authorize]`** — matches the codebase.

Phase 1 handlers:

| Handler | Shape | Notes |
|---|---|---|
| `proxy.listRoutes` | `Query()` → `Response(IReadOnlyList<RouteDto>)` | include stack name + live status |
| `proxy.getRoute` | `Query(int Id)` | `NotFound` if missing |
| `proxy.createRoute` | `Command(int StackId, string Domain, string ServiceName, int ContainerPort, bool TlsEnabled, bool IsPrimary, DomainKind Kind)` | validate: stack exists, domain unique, (optionally) service exists among the stack's running containers; insert; then `await caddy.ApplyAsync()` |
| `proxy.updateRoute` | `Command(int Id, …)` | reapply |
| `proxy.deleteRoute` | `Command(int Id)` → `Response(int Id)` | `ExecuteDeleteAsync`; disconnect container from edge net; reapply |
| `proxy.checkDns` | `Command(string Domain)` → `Response(bool ResolvesToHost, string? ResolvedIp)` | resolve + compare to host public IP; drives the preflight indicator |
| `proxy.getStatus` | `Query()` → `Response(bool CaddyRunning, string? CaddyVersion, int RouteCount)` | small overview panel |

Phase 2 handlers (new `Tenancy` module, or fold into `Proxy`): `templates.list/get/create/update/delete`,
`templates.addTenant`, `templates.syncInstances`, `templates.deployAll`, `templates.listTenants`.

## 5. Caddy management service

New singleton `CaddyManager`, registered in
`src/Watchtower.Application/WatchtowerServiceCollectionExtensions.cs` alongside the other infra
singletons (`AddSingleton<CaddyManager>()` + `AddHostedService(sp =>
sp.GetRequiredService<CaddyManager>())` — the same singleton+hosted-service idiom as
`DeployQueueService`). It never captures a scoped `DbContext`; it opens short-lived scopes via
`IServiceScopeFactory` exactly like `DeployQueueService` does.

Responsibilities:

1. **Reconcile on startup** (`StartAsync`): ensure `watchtower-control` + `watchtower-edge` networks
   exist; ensure Watchtower's own container is joined to `watchtower-control`; pull `caddy:2` if
   missing; ensure the `watchtower-caddy` container exists and is running (reuse if healthy, else
   (re)create — mirror `SelfUpdateService.ReconcileCoordinatorAsync`); reconnect all currently-running
   routed service containers to the edge network; push the current config.
2. **`ApplyAsync()`** — regenerate the Caddyfile from the `Route` table and reload Caddy via the admin
   API (`POST http://watchtower-caddy:2019/load`, `Content-Type: text/caddyfile`). Called from the
   route CRUD handlers and after each deploy.
3. **`ConnectStackAsync(stack)` / `DisconnectStackAsync(...)`** — find the routed service container(s)
   for a stack (list containers filtered by `com.docker.compose.project={ComposeProjectName}` +
   `com.docker.compose.service={ServiceName}`) and connect/disconnect them to the edge network with
   alias `{ComposeProjectName}-{ServiceName}`.

Supporting pieces:
- `CaddyConfigBuilder` — pure function `Build(IReadOnlyList<Route> routes, CaddyGlobals globals)` →
  Caddyfile string. Unit-testable, no I/O.
- **Reconcile loop** — a `BackgroundService` (`CaddyReconcileBackgroundService`) that periodically
  (a) refreshes route status from Caddy (cert presence/expiry via the admin API) and (b) re-runs
  DNS preflight for `AwaitingDns` routes, writing status back and pushing a status event. Use
  `BackgroundService` + `IOptionsMonitor<WatchtowerOptions>` to match the existing
  `StackUpdateBackgroundService` convention (the codebase uses `BackgroundService`, not `[ScheduledJob]`).

Generated Caddyfile shape:

```
{
    email {admin-email}          # from WatchtowerOptions; enables ACME
    admin 0.0.0.0:2019           # reachable only on watchtower-control
    # optional (Phase 2 white-label): on_demand_tls { ask http://watchtower:8080/api/proxy/ask }
}

{domain} {
    reverse_proxy {composeProjectName}-{serviceName}:{containerPort}
}
```

Caddy handles HTTP→HTTPS redirect and cert issuance/renewal automatically. `caddy_data:/data` persists
certs across restarts; `caddy_config:/config` autosaves the last pushed config.

## 6. `DockerEngineClient` extensions

`DockerEngineClient` (`src/Watchtower.Application/Services/DockerEngineClient.cs`) today can
create/start/inspect/wait/remove containers, pull images, and **list/inspect** networks — but has **no
network create/connect** and its create-DTOs lack the fields Caddy needs. Add (and **register every new
serialized type in `DockerJsonContext`, `DockerEngineClient.cs:533-563` — STJ source-gen is mandatory
or (de)serialization throws at runtime**):

1. `CreateNetworkAsync(name, labels)` → `POST /networks/create` (idempotent: check `ListNetworksAsync`
   first).
2. `ConnectContainerAsync(networkId, containerId, aliases)` → `POST /networks/{id}/connect`
   (`{ Container, EndpointConfig: { Aliases } }`); `DisconnectContainerAsync` → `.../disconnect`.
3. `ListContainersByLabelsAsync(filters)` → `GET /containers/json?filters=...` (for compose
   project/service lookup).
4. Extend `DockerCreateContainerBody` (`:755-762`) with `Labels`, `ExposedPorts`, and
   `NetworkingConfig` (or attach post-create via connect); extend `DockerCreateHostConfig` (`:765-778`)
   with `PortBindings` and `RestartPolicy`. Needed to publish Caddy's 80/443 and set restart policy.

Reuse `GetCurrentGroupIds()` (`SelfUpdateService.cs:307-317`) only if the managed container needs the
Docker socket — Caddy does **not**, so it's simpler than the coordinator (no socket bind, no GID juggling).

## 7. Deploy-pipeline integration

Hook the edge-network attach into the existing deploy flow. In
`DeployQueueService.ExecuteDeployAsync` (`DeployQueueService.cs:167-299`), after a successful
`_compose.UpAsync(...)` (~`:274-283`), if the stack has any `Route` rows, call
`caddy.ConnectStackAsync(stack)` then `caddy.ApplyAsync()`. `CaddyManager` is a singleton, so inject it
directly into the `DeployQueueService` constructor (no scope needed). This runs on every
deploy/recreate, so container re-creation transparently reconnects.

(The compose invocation itself is unchanged — we are **not** adding a `-f` override. If we ever do want
overrides for another reason, the single insertion point is `ComposeCliService.BuildArgs`,
`ComposeCliService.cs:67-75`, through which all four subcommands funnel.)

## 8. Deployment manifest

Because Watchtower manages the Caddy container itself (§2.6), **`deploy/docker/docker-compose.yml`
needs no Caddy service by default.** Requirements to document in the README/compose comments:
- Host ports **80 and 443 must be free** (Caddy binds them).
- The app already has the Docker socket + host docker GID (`group_add`), which is all it needs to
  create Caddy, its networks, and its volumes.
- Named volumes `caddy_data` / `caddy_config` are auto-created by Watchtower on first Caddy start.

Provide an **opt-out**: a `WatchtowerOptions` flag (e.g. `Proxy:Enabled`, and `Proxy:AdminEmail`) so an
operator who wants to run their own proxy can leave it off. Also document a compose-managed alternative
(operator declares Caddy + volumes + 80/443 themselves) for those who prefer to own it — in that mode
Watchtower only generates config and reloads, and skips container creation.

`Dockerfile` needs **no change** — Caddy runs as its own image; nothing new is bundled into the
Watchtower image.

## 9. Optional — custom domains + on-demand TLS (Phase 2 white-label)

For customer-owned domains at scale, add the on-demand path (kept optional per §2.3):
- Global Caddy option `on_demand_tls { ask http://watchtower:8080/api/proxy/ask }`, plus
  `tls { on_demand }` on custom-domain site blocks.
- New minimal-API endpoint `GET /api/proxy/ask?domain=` in
  `src/Watchtower.Api/Endpoints/WatchtowerHttpEndpoints.cs` (mirror the existing route registrations):
  return `200` iff the domain exists in the `Route` table (optionally: and is `AwaitingDns`/`Active`),
  else `403`. This gates issuance to registered domains only. Reachable by Caddy only on
  `watchtower-control`.
- Onboarding flow: create the `Route` (`Kind=Custom`, `Status=AwaitingDns`) → show the customer a
  copy-paste DNS target → the reconcile loop resolves and flips to `DnsOk`/`Active` → first request
  triggers issuance via `ask`.

For our own subdomains, explicit blocks (§2.3) remain the default even in Phase 2.

## 10. Frontend — `Proxy` UI module

New module `src/watchtower-web/src/modules/proxy/{index.ts, module.tsx, RoutesPage.tsx, RouteForm.tsx}`,
mirroring `modules/stacks/`:
- `module.tsx`: `defineModule({ name: 'Proxy', when: { module: 'Proxy' }, contributes: [
  contribute(sidebarItems, [{ id: 'routes', label: 'Routes', icon: Globe, to: '/routes', order: 25 }]) ] })`
  plus a `createRoute({ path: '/routes', beforeLoad: redirectUnless({ module: 'Proxy' }, '/'), … })`.
- `index.ts`: `export default { manifest, routes } satisfies AppModule`.
- **Register routes**: add `import proxy from '@/modules/proxy'` and `...proxy.routes` to
  `src/watchtower-web/src/platform/router.tsx` `addChildren` (manifests are auto-discovered by glob;
  routes are the only manual step). `'Proxy'` must exist in the generated `ModuleName` union, which
  happens automatically once the backend ships `[AppModule("Proxy")]` and the schema is re-exported.
- **API methods**: add a `proxy` slice to `src/watchtower-web/src/lib/api.ts` (`listRoutes`,
  `createRoute`, `deleteRoute`, `checkDns`, …) over the typed `rpc(...)` client.

The form (the whole UX):
```
Domain:  [ app.example.com ]     ← inline DNS preflight (proxy.checkDns): ✓ points here / ✗ not yet
Service: [ ▾ web  (stack: myapp) ]  ← dropdown from the selected stack's running services
Port:    [ ▾ 3000 (detected) ]
         [x] HTTPS (automatic)
```
Service/port options come from `containers.list` filtered to the stack's project (MVP), or a small new
`proxy.stackServices` helper. Live cert/route status: reuse the existing SSE pattern — a
`ProxyStatusBroadcaster` (clone of `DeployOutputBroadcaster`) + a `GET /api/proxy/routes/{id}/status`
stream (clone of `MapDeployOutputStream`) rendered with the existing `<LiveLog doneEvent="done" />`
component. Alternatively, poll `proxy.listRoutes` on an interval — status changes are low-frequency.

## 11. Schema regeneration + CI

After the backend handlers exist, regenerate the RPC schema **from the repo root** (the exporter writes
the path relative to the process CWD; running it from `src/Watchtower.Api` silently writes a stale file
that the frontend then builds against — CI's `git diff` check catches drift but locally it's silent):

```bash
dotnet run --project src/Watchtower.Api -- --export-schema rpc-schema.json   # run from repo root
```

The frontend `prebuild` regenerates its client from `../../rpc-schema.json` automatically on
`npm run build`. CI already enforces schema freshness (`.github/workflows/ci.yml:36-38`).

## 12. Milestones / task breakdown

**Phase 1 — core reverse proxy**
1. `DockerEngineClient`: network create/connect/disconnect, label-filtered container list, extended
   create-DTOs, `DockerJsonContext` registrations. (Foundational; unblocks everything.)
2. `Route` entity + `RouteConfiguration` + EF migration.
3. `CaddyConfigBuilder` (+ unit tests) and `CaddyManager` (lifecycle, reconcile, apply, connect).
4. `Proxy` module: contracts, JSON context, handlers (`listRoutes`/`getRoute`/`createRoute`/
   `updateRoute`/`deleteRoute`/`checkDns`/`getStatus`).
5. Deploy-pipeline hook (`ConnectStackAsync` + `ApplyAsync` after `UpAsync`).
6. Schema re-export; frontend `proxy` module + `api.ts` slice + `router.tsx` registration + form +
   live status.
7. Deploy-manifest docs + `Proxy:Enabled`/`Proxy:AdminEmail` options + opt-out path.
8. Verify end-to-end: create a stack, add a route, confirm HTTPS + isolation (service has no published
   host ports).

**Phase 2 — templated multi-tenancy**
9. `StackTemplate` (+ `StackTemplateEnvVar`) entity, `Stack.TemplateId`/`TenantSlug`, migration.
10. `Tenancy` handlers (create template, add tenant, sync, deploy-all).
11. Per-tenant ingress networks (`watchtower-ingress-{stackId}`) for L2 isolation.
12. Optional white-label: on-demand TLS + `/api/proxy/ask`, custom-domain onboarding UI.
13. Tenant UI: template detail + tenants list + one-click add-tenant + deploy-all progress.

## 13. Risks / open questions

- **Host ports 80/443** must be free; if the operator already runs a proxy there, they must use the
  compose-managed/opt-out mode. Document loudly.
- **Watchtower self-join to `watchtower-control`**: confirm the app can determine its own container id
  (hostname = container id by default; the self-update code already detects the running container —
  reuse that detection).
- **Caddy admin API exposure**: keeping admin on `watchtower-control` (not the ingress network) is
  essential so tenant containers can't reconfigure Caddy. Verify tenants are never attached to
  `watchtower-control`.
- **Multiple replicas of a routed service**: MVP assumes one container per routed service; scaled
  services need multiple upstreams (defer).
- **Cert status readback**: expiry/issued state via the Caddy admin API (`/config`, `/pki`)—confirm the
  exact endpoints during implementation.
- **DNS preflight "host public IP"**: how Watchtower learns its own public IP (config value vs
  detection) needs a decision; a configured `Proxy:PublicIp`/`Proxy:BaseDomain` is the simplest.
