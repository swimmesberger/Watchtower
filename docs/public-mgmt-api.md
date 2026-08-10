# The public Management API (`/api/mgmt/*`)

A stack that Watchtower deploys can also manage **the tenants of one designated stack template**:
list them, provision a new one (subdomain + environment overrides), inspect status, trigger a
redeploy, stream logs, and deprovision. This is the *Management API* — a token-authenticated REST
surface under `/api/mgmt/`, and the multi-tenant counterpart to the self-scoped
[App API](public-app-api.md).

It exists so a product vendor can ship a **central management UI as an ordinary Watchtower stack**.
The UI signs up a customer, Watchtower provisions the tenant in the background, and the UI shows the
result — without anyone holding operator credentials, and without an operator having to click
through Watchtower for every signup. Watchtower stays the orchestrator; your UI is the product.

> **A management stack sees exactly one template's tenants.** Authority comes from an
> operator-created *grant* linking your stack to a template, evaluated on every request — never from
> the token alone. Templates you were not granted are indistinguishable from templates that do not
> exist. No response contains environment variable values, deploy output, credentials, or any stack
> that is not a tenant of a granted template.

## Authentication

Every request carries **the same bearer token as the App API** — your own stack's token, the one
Watchtower injects into your environment at every deploy as `WATCHTOWER_APP_TOKEN`:

```
Authorization: Bearer wtapp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

There is no second credential to mint, distribute, or rotate. See
[the App API's authentication section](public-app-api.md#authentication) for how the token is
generated, injected, and rotated — all of it applies here unchanged, including that a rotated token
only reaches your containers at their **next deploy**.

**The token alone grants nothing here.** It proves *which stack you are*; the grant decides *what
you may manage*. A perfectly valid token with no grant can only ever see an empty template list.

| Situation | Response |
| --- | --- |
| Header missing, not `Bearer`, or not a `wtapp_` token | `401` |
| Token does not match any stack | `401` |
| Token matches a stack whose App API is switched off | `403` |
| Template does not exist **or** you have no grant on it | `404` |
| Tenant slug does not exist **under that template** | `404` |
| `DELETE` on a grant without `allowDelete` | `403` |

Error bodies are JSON: `{ "error": "…" }`.

**The `404` for an ungranted template is deliberate.** A `403` would confirm that template `17`
exists on this host; a uniform `404` tells you nothing about templates you were not granted. For the
same reason a tenant slug is always resolved *within* the granted template — asking a template you
manage for a slug that belongs to a different one is a `404`, not a permission error.

**One kill switch covers both surfaces.** `AppApiEnabled` is per stack, not per surface: turning it
off with `stacks.setAppApi` closes `/api/mgmt/*` *and* that stack's own `/api/app/*` reads
immediately (`403`). To cut management access while leaving self-reads working, revoke the grant
instead. Only the **calling** stack's flag is consulted — switching the App API off on a *tenant*
stops that tenant querying itself, but does not hide it from, or protect it against, the manager.

### Status codes

| Code | Meaning |
| --- | --- |
| `200` | Success. |
| `201` | Tenant created (`POST …/tenants`). |
| `202` | Deploy accepted and queued (`POST …/deploy`). |
| `400` | Invalid slug, invalid env variable name, malformed body; on `/logs`, several services and no `service`. |
| `401` | Missing, malformed, or unknown token; on `/tenants/accessible`, an unusable user assertion. |
| `403` | App API switched off for your stack, or `DELETE` without `allowDelete`. |
| `404` | Unknown/ungranted template, unknown tenant slug; on `/logs`, no container matches; on `/tenants/accessible`, central auth is off. |
| `409` | Slug, domain, or stack name already taken; `DELETE` while a deploy is queued or running. |
| `500` | Only on `DELETE`: the compose teardown failed. Nothing was deleted; the call is safe to retry. |
| `503` | The Docker daemon is unreachable, so live container state could not be read. |

Endpoints that read only Watchtower's own database — the template list, the tenant list, and
`/deployments` — keep working while Docker is down.

## Granting access

Grants are **operator-only**. They are created from the admin JSON-RPC API (`POST /rpc`), which the
management stack itself cannot reach; when authentication is enabled these three methods require the
`admin` role.

| Method | Request | Response |
| --- | --- | --- |
| `templates.listGrants` | `{ templateId }` | `{ grants: [{ stackId, stackName, allowDelete, createdAt }] }` |
| `templates.grantManagement` | `{ templateId, stackId, allowDelete }` | `{ grant: { … } }` |
| `templates.revokeManagement` | `{ templateId, stackId }` | `{ removed: true }` |

`templates.grantManagement` is an **upsert**: calling it again on an existing pair updates
`allowDelete`, which is how you turn deprovisioning on and off. `templates.revokeManagement` is
idempotent and answers `{ "removed": false }` when there was no grant.

- **`allowDelete` is the only capability flag.** Without it a grant is read/create/deploy/logs;
  `DELETE` answers `403`. Everything else comes as one bundle.
- **A tenant may not be granted management of its own template.** Granting template `T` to a stack
  that is itself an instance of `T` is rejected — it would let one customer list, redeploy, read the
  logs of, and possibly delete its neighbours. Grant the template to a *separate* management stack.
- **Revocation is immediate**, because the grant is checked on every request. Unlike rotating a
  token, you do not have to wait for a deploy.

```bash
# Let stack 12 (the vendor's management UI) manage template 3, including teardown.
curl -sS -X POST "$WATCHTOWER_URL/rpc" -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1, "method": "templates.grantManagement",
  "params": { "templateId": 3, "stackId": 12, "allowDelete": true }
}'

# Who can manage template 3?
curl -sS -X POST "$WATCHTOWER_URL/rpc" -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 2, "method": "templates.listGrants", "params": { "templateId": 3 }
}'
```

## Endpoints

All responses are JSON except `/logs`, which is a Server-Sent-Event stream. Every path is scoped by
`{templateId}`, and every one of them `404`s unless you hold a grant on that template. Two of them
list tenants and they answer different questions: [`…/tenants`](#get-apimgmttemplatestemplateidtenants)
is the **operations view** — every tenant of the template, whoever is asking — while
[`…/tenants/accessible`](#get-apimgmttemplatestemplateidtenantsaccessible) is **one user's view**,
filtered to the tenants a named, proven visitor may actually open.

### `GET /api/mgmt/templates`

The templates you may manage — nothing else on the host appears, and an ungranted caller gets an
empty list rather than an error.

```json
{
  "templates": [
    {
      "id": 3,
      "name": "saas-app",
      "domainPattern": "{tenant}.example.com",
      "targetServiceName": "web",
      "allowDelete": true,
      "tenantCount": 12
    }
  ]
}
```

`allowDelete` is your grant's flag, not a property of the template: it tells you up front whether
`DELETE` will be accepted.

### `GET /api/mgmt/templates/{templateId}/tenants`

Every tenant of the template, newest first.

```json
{
  "tenants": [
    {
      "slug": "customer4",
      "stackId": 41,
      "domain": "customer4.example.com",
      "lastDeployStatus": "success",
      "lastDeployedAt": "2026-08-10T09:14:02.118+00:00",
      "createdAt": "2026-08-03T11:20:44.900+00:00"
    }
  ]
}
```

`lastDeployStatus` is `null` for a tenant that has never deployed. Every status value in this API
comes from the same vocabulary as the App API: `queued`, `running`, `success`, `failed`.

### `GET /api/mgmt/templates/{templateId}/tenants/accessible`

The same template's tenants, but **filtered to what one visiting user may reach** — the support
agent's view, and the management-stack counterpart to
[`GET /api/app/tenants/accessible`](public-app-api.md#get-apiapptenantsaccessible). Where
`…/tenants` answers "which customers exist", this answers "which of them may the person in front of
me open".

The user is never named by id. You prove them with the [central-auth](central-auth/README.md)
identity assertion from the request your UI is currently serving, so this endpoint takes a second
header alongside your bearer token:

```
X-Watchtower-Jwt: <the assertion, forwarded verbatim>
```

```json
{
  "tenants": [
    { "slug": "customer4", "domain": "customer4.example.com" },
    { "slug": "customer7", "domain": "customer7.example.com" }
  ]
}
```

Sorted by `slug` ascending. There is no `current` field — a management stack is not one of the
tenants it lists, so the App API's "this one is me" marker has nothing to mark. `Public` and
`Authenticated` tenants are always listed; a `Restricted` one only when that user holds a grant on
it; a tenant with no primary route is omitted. As on the App API, the payload is meant to be
rendered to a person, so it carries no stack ids, status, or timestamps.

**The grant comes first.** Ordinary [`404` semantics](#authentication) are unchanged and are decided
*before* the assertion is looked at: a template you were not granted — or that does not exist — is a
`404`, so this endpoint tells you nothing new about the host. Only once the grant holds is the
assertion checked.

| Situation | Response |
| --- | --- |
| Unknown or ungranted template | `404` (as everywhere else here) |
| Central auth is switched off on this Watchtower | `404` |
| The assertion is missing, expired, tampered with, minted for another domain, or names a disabled user | `401` `{"error":"Missing or invalid user assertion."}` |

**The audience must be yours.** An assertion is bound to the domain it was issued for, and this
endpoint accepts it only when that `aud` is one of the **management stack's own route domains** —
the domain your UI is served on, not the tenant's. So you can ask about somebody visiting your
management UI, and nothing lets you ask about somebody visiting a tenant. One generic `401` covers
every assertion failure, deliberately: the endpoint must not report *which* check failed. See
[ADR-0011](decisions/0011-user-scoped-tenant-discovery.md).

### `POST /api/mgmt/templates/{templateId}/tenants`

Provision a tenant. The slug becomes the subdomain (substituted into the template's
`domainPattern`), the compose project name, and the tenant's identity in every other endpoint here.

```json
{
  "slug": "customer4",
  "env": { "PLAN": "pro", "SEATS": "25" }
}
```

`env` is optional. Its entries are merged **over** the template's base environment variables for this
tenant only. Response `201`:

```json
{
  "slug": "customer4",
  "stackId": 41,
  "domain": "customer4.example.com",
  "deploy": { "id": 87, "status": "queued" }
}
```

Creation is **asynchronous**: the stack, its environment, and its route are written in one
transaction and the first deploy is queued. Poll the tenant's status (below) to see it finish.

- Slugs must start with a letter or digit and contain only lowercase letters, digits, and hyphens;
  anything else is `400`.
- **`accessible` is a reserved slug** and is refused with `400` — it names the user-filtered listing
  endpoint above, so a tenant called that could never be addressed at `…/tenants/accessible`.
- Env keys must be valid variable names — `^[A-Za-z_][A-Za-z0-9_]*$` — or the request is `400`.
  Because `env` is a JSON *object*, a key repeated in the same body is not an error: the JSON parser
  keeps the last occurrence, so the request succeeds with that value.
- A slug already used under this template, a domain already routed, or a colliding stack/compose
  project name is `409`.
- **Env override values are write-only.** They are never returned by this API, by any later read of
  the tenant, or in any error message. If your UI needs to show them, keep your own copy.
- **The tenant gets its Watchtower variables with no compose changes.** `WATCHTOWER_APP_TOKEN`,
  `WATCHTOWER_STACK_ID` and `WATCHTOWER_URL` are injected directly into the template's
  `targetServiceName` — see
  [injected environment variables](public-app-api.md#injected-environment-variables).

### `GET /api/mgmt/templates/{templateId}/tenants/{slug}`

One tenant's deployment status plus live container state — the same payload the tenant itself would
get from `GET /api/app/status`, with its identity added.

```json
{
  "slug": "customer4",
  "domain": "customer4.example.com",
  "lastDeployStatus": "success",
  "lastDeployedAt": "2026-08-10T09:14:02.118+00:00",
  "lastDeployedCommit": "9f1c2ab7d4e5…",
  "activeDeploy": null,
  "services": [
    {
      "service": "web",
      "containerId": "6f0b1c…",
      "state": "running",
      "status": "Up 12 minutes",
      "image": "ghcr.io/example/app:latest"
    }
  ]
}
```

`activeDeploy` is `{ "id": 88, "status": "running", "startedAt": "…" }` while a deploy is queued or
running, and `null` otherwise. Container ids are reported but are never accepted as input: containers
are resolved server-side from the tenant's compose project.

### `POST /api/mgmt/templates/{templateId}/tenants/{slug}/deploy`

Queue a redeploy of one tenant. Returns `202` immediately — it does not wait for the deploy.

```json
{ "deploy": { "id": 88, "status": "queued" } }
```

The deploy is recorded with `triggeredBy: "mgmt-deploy"`. Watchtower coalesces per stack: at most one
deploy runs per tenant with a single pending slot, so hammering this endpoint cannot pile up work.

### `GET /api/mgmt/templates/{templateId}/tenants/{slug}/deployments?limit=`

One tenant's deploy history, newest first. `limit` defaults to `20` and is capped at `100`.

```json
{
  "deployments": [
    {
      "id": 87,
      "status": "success",
      "triggeredBy": "mgmt-deploy",
      "startedAt": "2026-08-10T09:13:38.002+00:00",
      "finishedAt": "2026-08-10T09:14:02.118+00:00"
    }
  ]
}
```

The captured command output of a deploy is **never** returned — it is produced with git and registry
credentials in scope. Operators can still read it in the Watchtower UI.

### `GET /api/mgmt/templates/{templateId}/tenants/{slug}/logs?service=&tail=&follow=`

One tenant's container logs as Server-Sent Events. The contract is **identical** to the App API's log
stream — same query parameters and defaults, same `400` when the tenant exposes several services and
no `service` was given, same `404` when nothing matches, same short-id prefixes for replicas, same
per-source `event: error` frames and terminal `event: done`. It is the same implementation, pointed
at the tenant's compose project instead of your own.

Rather than repeat it, read
[`GET /api/app/logs`](public-app-api.md#get-apiapplogsserviceservicetailtailfollowfollow) and
[When something fails mid-stream](public-app-api.md#when-something-fails-mid-stream); the JavaScript
consumer example there works here by changing only the URL.

### `DELETE /api/mgmt/templates/{templateId}/tenants/{slug}?volumes=`

Deprovision a tenant: stop and remove its containers, then delete it from Watchtower.

```json
{ "slug": "customer4", "deleted": true }
```

| Query | Default | Effect |
| --- | --- | --- |
| `volumes` | `false` | `true` also removes the tenant's compose volumes — **the customer's data is gone**. |

Preconditions, both enforced server-side:

- **Your grant must have `allowDelete`.** Otherwise `403`
  `{"error":"Delete is not permitted for this grant."}`. It is visible ahead of time on
  `GET /api/mgmt/templates`.
- **No deploy may be in flight.** A tenant with a queued or running deploy is `409`; wait for
  `activeDeploy` to go `null` and retry. This is a check, not a lock: a deploy that starts in the
  narrow window right after it passes can bring the tenant's containers back up *after* teardown
  finishes, leaving orphans an operator has to clear by hand. Stop deploying a tenant before you
  delete it.

Teardown runs in a fixed order — refuse if deploying, `docker compose down --remove-orphans` (plus
`--volumes` when asked), delete the tenant's database rows (route, environment, deploy history), then
re-apply the proxy configuration so the domain stops being served. **If the compose step fails,
nothing is deleted**: the response is `500`, the containers are still up, the tenant row is intact,
and the call is safe to retry. The compose output is logged on the host and never returned, for the
same reason deploy output never is.

## Examples

Everything below uses the two variables your management stack already has in its environment —
`WATCHTOWER_APP_TOKEN` and `WATCHTOWER_URL` (the latter needs `Watchtower:PublicBaseUrl` to be
configured on the host).

```bash
AUTH=(-H "Authorization: Bearer $WATCHTOWER_APP_TOKEN")

# What may I manage? (empty list = no grant yet — ask your operator)
curl -sS "${AUTH[@]}" "$WATCHTOWER_URL/api/mgmt/templates"

# All customers of template 3
curl -sS "${AUTH[@]}" "$WATCHTOWER_URL/api/mgmt/templates/3/tenants"

# ...and only the ones THIS visitor may open ($VISITOR_JWT is the X-Watchtower-Jwt of the
# request your UI is serving, not a stored value)
curl -sS "${AUTH[@]}" -H "X-Watchtower-Jwt: $VISITOR_JWT" \
  "$WATCHTOWER_URL/api/mgmt/templates/3/tenants/accessible"

# Sign-up: provision "customer4" with two per-tenant overrides
curl -sS "${AUTH[@]}" -X POST -H 'Content-Type: application/json' \
  "$WATCHTOWER_URL/api/mgmt/templates/3/tenants" \
  -d '{"slug":"customer4","env":{"PLAN":"pro","SEATS":"25"}}'

# Did the first deploy land? (poll until activeDeploy is null)
curl -sS "${AUTH[@]}" "$WATCHTOWER_URL/api/mgmt/templates/3/tenants/customer4"

# Push the new release to that one customer
curl -sS "${AUTH[@]}" -X POST \
  "$WATCHTOWER_URL/api/mgmt/templates/3/tenants/customer4/deploy"

# Support ticket: last 200 lines from the "web" service
curl -sS -N "${AUTH[@]}" \
  "$WATCHTOWER_URL/api/mgmt/templates/3/tenants/customer4/logs?service=web&tail=200"

# …or follow it live
curl -sS -N "${AUTH[@]}" \
  "$WATCHTOWER_URL/api/mgmt/templates/3/tenants/customer4/logs?service=web&follow=true"

# Churn: remove the tenant but keep its volumes (the default)
curl -sS "${AUTH[@]}" -X DELETE \
  "$WATCHTOWER_URL/api/mgmt/templates/3/tenants/customer4"

# Churn, for real: containers *and* data
curl -sS "${AUTH[@]}" -X DELETE \
  "$WATCHTOWER_URL/api/mgmt/templates/3/tenants/customer4?volumes=true"
```

## Exposing it

Watchtower has no built-in authentication and is normally placed behind an authenticating reverse
proxy. Like the deploy webhook and the App API, the Management API is designed to be reachable by a
caller that cannot pass that proxy's login — it carries its own bearer token. If your proxy fronts
Watchtower, allow `/api/mgmt/*` through unauthenticated (the bearer token is the gate), or route the
management stack to Watchtower over an internal network instead.

There is deliberately **no rate limiting** on these routes today; put one in the proxy if the API is
reachable from the public internet. Note that unlike the App API these routes *write* — a valid token
with a grant can create tenants and queue deploys — so throttling matters more here.
