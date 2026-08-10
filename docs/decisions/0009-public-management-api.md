# ADR-0009: A management stack manages one template's tenants through a granted REST API

- Status: Accepted
- Date: 2026-08-10
- Related: [ADR-0008](0008-public-app-api.md) (the self-scoped sibling whose credential this reuses).

## Context

A product vendor wants to ship a *central management UI* — a stack that lists its customers, creates
a new one on signup, shows whether the last deploy worked, streams a tenant's logs when support asks,
and removes a customer that churned. Watchtower already models exactly that shape: a `StackTemplate`
plus one tenant `Stack` per customer, each with its own compose project, route, and env.

Nothing lets the vendor's UI drive it. The App API ([ADR-0008](0008-public-app-api.md)) is
deliberately self-only — a stack sees its own status and nothing else — so the management UI can
read itself and no tenant. The JSON-RPC admin API can do all of it, but it is the operator API:
stack-agnostic, fronted by a proxy the stack cannot log into, and handing it over would give the
vendor's UI every stack on the host, every credential, and Watchtower's own self-update. The
remaining option is out-of-band operator work for every signup, which is what the vendor is trying
to stop doing.

There is also a gap underneath: **no tenant teardown exists**. The admin `stacks.delete` is
database-only — the containers keep running and Caddy keeps serving the dead route — so
"deprovision a customer" cannot be exposed until a real teardown path exists.

What the management UI legitimately needs is narrow but not self-referential: the tenants of *one
designated template*, and nothing else on the host.

## Decision

**Add a public Management API: a REST surface under `/api/mgmt/*`, authenticated by the caller
stack's existing App API bearer token and authorized by an operator-granted link between that stack
and a single `StackTemplate`.**

- **The credential is reused; the authority is not.** The caller presents the same
  `WATCHTOWER_APP_TOKEN` (`wtapp_…`) it already receives at every deploy. The token by itself still
  grants only the self-scoped App API — every `/api/mgmt/*` request additionally requires a grant.
  Nothing new has to be minted, distributed, or rotated.
- **Authorization is a stored grant, not a claim in the token.** A new `TemplateManagementGrant`
  entity (`StackId`, `TemplateId`, `AllowDelete`, unique per pair; precedent `RouteAccessGrant`)
  is the whole authorization model. It is evaluated per request, so revoking a grant takes effect
  immediately — unlike token rotation, which only reaches containers at their next deploy.
- **Only operators create grants**, through three admin-role JSON-RPC methods:
  `templates.listGrants`, `templates.grantManagement`, `templates.revokeManagement`. A stack cannot
  grant itself anything, and **a tenant of a template may not be granted management of that same
  template** — that would let one customer read and delete its neighbours.
- **Ungranted templates return `404`, never `403`.** A caller cannot distinguish "no such template"
  from "not yours", so the surface leaks no information about the host's other templates. `401`
  (unknown token) and `403` (token valid, App API switched off — or `DELETE` without `AllowDelete`)
  keep the ADR-0008 meanings.
- **Every lookup is constrained by the granted template.** Tenants are resolved as
  `TemplateId == <granted id> AND TenantSlug == <slug>`, never by stack id, so a slug belonging to
  another template is a `404` rather than an authorization decision. Container and log access is
  performed through a synthesized App API caller for the *tenant* stack, which reuses the existing
  compose-project scoping unchanged.
- **Destruction is a separate capability.** `AllowDelete` gates `DELETE`, and the delete path is a
  new `TenantTeardownService` that refuses while a deploy is queued or running, runs
  `docker compose -p <project> down --remove-orphans` (with `--volumes` only when asked), deletes the
  stack row, and re-applies the Caddy config so the proxy stops serving the dead route. If
  compose-down fails, the row survives — a retryable state beats an orphaned container set.
- **Plain minimal-API endpoints, logic in Application services**, per ADR-0003 and ADR-0008: an
  externally authenticated surface with its own auth semantics, one of whose endpoints is a stream.
  The App API's SSE log machinery is *extracted* into a shared helper used by both surfaces rather
  than duplicated, leaving `/api/app/logs` byte-for-byte unchanged.
- **The never-return list from ADR-0008 carries over verbatim**, and now covers tenants: no
  environment variable values (creation accepts overrides, and they are write-only), no deploy
  output, no credentials, no stack that is not a tenant of a granted template.

## Consequences

- **A vendor can self-serve its own customers without operator credentials.** Provisioning, status,
  redeploy, logs, and teardown for one template's tenants; nothing else on the host is addressable,
  even by id.
- **One credential now unlocks two surfaces.** The stack's `wtapp_` token is no longer only a
  read-only self-token: for a granted stack it also creates and destroys tenants. A leaked
  management-stack token therefore reaches every tenant of the granted template, including their
  logs. The mitigations are deliberate but blunt — the grant is operator-controlled and revocable in
  one RPC call, and `AppApiEnabled` is a **single kill switch that closes both surfaces at once**.
  There is no way to disable a stack's management access while leaving its self-reads working, other
  than revoking the grant.
- **No rate limiting**, consistent with the webhook and the App API. Tenant *creation* is now
  reachable from an unauthenticated-at-the-proxy route, so an attacker holding a valid token can
  queue deploys; throttling is the reverse proxy's job and the operator doc says so.
- **Tenant teardown is the first real compose-down path in the codebase.** Everything else deploys or
  reads; this removes running infrastructure, optionally with volumes, on an HTTP request. The
  ordering (refuse-if-deploying → compose down → delete row → re-apply Caddy) is the contract, it is
  pinned by tests through a stubbable compose seam, and the admin `templates.removeTenant` handler
  shares the same service so operators and the API cannot diverge.
  **Residual risk:** the active-deploy refusal is a check, not a lock. A deploy enqueued in the window
  between that check passing and compose-down completing can recreate the tenant's containers after
  teardown has already deleted the row, leaving orphans the operator must remove manually with
  `docker compose -p <project> down`. Accepted rather than engineered around — closing it needs a
  per-stack lock spanning the queue and the teardown, the window is small, and the failure mode is
  leftover containers on the host, not data loss or a security boundary crossing.
- **The management stack must be deployed by the same Watchtower it controls**, because its authority
  is bound to a stack row and its credential arrives through that stack's deploy pipeline. A UI
  running anywhere else — a laptop, a CI job, another host — cannot use this API at all. Standalone
  service tokens would lift the restriction later, behind this same grant model.
- **Grants are the only knob.** `AllowDelete` is the only capability flag; read, create, deploy, and
  log access come as one bundle. Finer scopes can be added to the grant row later without changing
  the surface.

### Rejected alternatives

- **Expose the admin JSON-RPC surface to token-bearing callers.** One flag on the token would turn
  the vendor's UI into an operator: every stack, every credential, registry secrets, Watchtower's own
  self-update. The blast radius of a leaked token would jump from one template's tenants to the whole
  host, and every future admin method would silently join the public surface.
- **Mint standalone service tokens now**, unbound from any stack, so the management UI could run
  anywhere. It reintroduces the problem the App API avoided: a secret an operator has to generate,
  hand over, store, and rotate out of band, with no delivery mechanism to make rotation safe. Bound
  to a stack, the credential is delivered and rotated by the deploy pipeline that already exists.
  Deferred to central-auth Phase 2, which can add service identities *behind the same grant model*
  rather than beside it.
- **Put the endpoints under `/api/app/*`.** Convenient — same token, same middleware — but it would
  destroy that surface's one-sentence security contract, *a stack can only ever see itself*. Reviewers
  and operators rely on that invariant to reason about the App API without reading the code; a
  surface where some tokens see other stacks is a different thing and gets a different prefix.
