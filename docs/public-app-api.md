# The public App API (`/api/app/*`)

Applications that Watchtower deploys can ask Watchtower about **themselves**: their deployment
status, the version they are currently running, their recent deploy history, and their container
logs. This is the *App API* — a small token-authenticated REST surface under `/api/app/`.

It exists so an application can render its own "deployed version" badge, expose a richer health page,
or wait for an in-flight deploy to settle, without being handed operator credentials or access to the
JSON-RPC admin API.

> **A stack can only ever see itself.** Container ids are never accepted from the caller: every
> Docker lookup is resolved server-side from the authenticated stack's compose project label. No
> response contains deploy output, environment variable values, credentials, or another stack's data.
> The one exception is [`/tenants/accessible`](#get-apiapptenantsaccessible)
> ([ADR-0011](decisions/0011-user-scoped-tenant-discovery.md)), which answers *on behalf of a proven
> visiting user* — never on the stack's own account — and only about siblings that user could reach
> by typing their domains anyway.

## Authentication

Every request carries the stack's own bearer token:

```
Authorization: Bearer wtapp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

The token is `wtapp_` followed by 32 cryptographically random bytes in unpadded base64url. It is
minted when the stack is created (or lazily at the next deploy, for stacks that predate this
feature) and **injected into the stack's environment on every deploy** — the application does not
store it, it reads it from `WATCHTOWER_APP_TOKEN`.

| Situation | Response |
| --- | --- |
| Header missing, not `Bearer`, or not a `wtapp_` token | `401` |
| Token does not match any stack | `401` |
| Token matches a stack whose App API is switched off | `403` |

Error bodies are JSON: `{ "error": "…" }`.

### Status codes

Every endpoint can return these:

| Code | Meaning |
| --- | --- |
| `200` | Success. |
| `400` | Only on `/logs`: the stack exposes several services and no `service` was given. The body lists them. |
| `401` | Missing, malformed, or unknown token — or, on `/tenants/accessible`, an unusable user assertion. |
| `403` | Valid token, but the App API is switched off for that stack. |
| `404` | On `/logs`: no container matches (the stack is down, or the named service does not exist). On `/tenants/accessible`: the caller is not a tenant, or central auth is off. |
| `503` | The Docker daemon is unreachable, so live container state could not be read. Affects `/status`, `/version` and `/logs`. |

`/self` and `/deployments` read only Watchtower's own database, so they keep working while Docker is
down. If the daemon fails *after* a log stream has started, the headers are already sent and the
failure is reported in-band as an `event: error` frame instead (see [`/logs`](#get-apiapplogsserviceservicetailtailfollowfollow)).

Tokens are stored in plaintext on the stack row, like `WebhookToken` and registry credentials —
Watchtower has to read the value back to re-inject it at every deploy. See
[ADR-0008](decisions/0008-public-app-api.md).

Operators manage the token from the admin JSON-RPC API:

- `stacks.getAppApi` → `{ enabled, token, injectedVarNames }`
- `stacks.setAppApi` → same shape; accepts `{ stackId, enabled?, regenerateToken? }`

Rotating a token takes effect for the running containers only at their **next deploy** — until then
they keep presenting the old value and will receive `401`.

## Injected environment variables

Watchtower puts these **into your containers itself**. At deploy time it generates a small compose
override file and merges it into the deploy with a second `-f`, so the variables land straight in the
`environment:` of the services that should have them — your own compose file does not have to mention
them at all.

| Variable | Value | Which services receive it |
| --- | --- | --- |
| `WATCHTOWER_STACK_ID` | Watchtower's numeric stack id | every service, always |
| `WATCHTOWER_URL` | Watchtower's public base URL | every service, when `Watchtower:PublicBaseUrl` is configured |
| `WATCHTOWER_APP_TOKEN` | The stack's App API bearer token | the services chosen by the rules [below](#which-services-receive-the-token) |

Set the base URL with `WATCHTOWER__PUBLICBASEURL=https://watchtower.example.com` (or the
`Watchtower:PublicBaseUrl` config key). Without it the variable is simply not injected and the
application must know where Watchtower lives by other means.

All three are *also* written into the temporary `.env` Watchtower passes to
`docker compose --env-file`, **after** the operator's own stack variables, so they stay available for
[interpolation](#interpolation-still-works-if-you-want-to-place-them-yourself). Reserved names always
win: an operator variable using one of these keys is skipped.

### Which services receive the token

`WATCHTOWER_STACK_ID` and `WATCHTOWER_URL` are not secrets, so every service gets them. The token is
a credential — on a stack with a management grant it can create and delete tenants — so it reaches
only the services chosen like this:

- Every service labelled `watchtower.inject-token: "true"` receives it. Label as many as you like.
- A service labelled `watchtower.inject-token: "false"` **never** receives it, whatever the defaults
  below would have said. This is the opt-out.
- Both values are matched **case-insensitively, with surrounding whitespace ignored**, so `"True"`
  and `" false "` do what you expect.
- If **no** service is labelled `"true"`, exactly one default applies:
  - a **tenant** of a template → the template's `targetServiceName`, if the compose file defines it;
  - a **plain stack with exactly one service** → that service;
  - a **plain stack with several services** → *no service*. Watchtower will not guess which container
    is the application; label one, or pass the variable through yourself. "One service" means one
    service in the **resolved** configuration, so if you use `profiles:`, do not lean on this
    default — whether a service behind an inactive profile is counted depends on your compose
    version, and that can silently flip a stack between the one-service and several-services rules.
    Set the label instead.

```yaml
services:
  web:
    image: ghcr.io/example/app:latest
    labels:
      watchtower.inject-token: "true"    # the app itself: give it the token
  worker:
    image: ghcr.io/example/worker:latest
    labels:
      watchtower.inject-token: "false"   # never, even if it were the only service
```

Anything other than `"true"` or `"false"` is treated as if the label were absent, and the deploy
output says so. A tenant whose template names a target service the compose file does not define also
gets a warning line and no default token injection — so when the token is missing, the deploy output
is the first place to look. It carries one line per service, naming the variables that service
received; values are never printed:

```
[Watchtower] Injecting WATCHTOWER_APP_TOKEN, WATCHTOWER_STACK_ID into service 'web'
```

### Your repository's own `.env` still works

Passing `--env-file` makes Compose stop auto-loading the project directory's `.env`. Because
Watchtower now always passes one, it **merges your repository's committed `.env` into the file it
generates**, so a repo that ships a `.env` keeps working unchanged. Precedence, lowest first:

1. the repository's `.env` (copied through verbatim, so quoting and escaping survive as written),
2. the stack's operator-defined variables,
3. Watchtower's reserved `WATCHTOWER_*` variables.

A key defined at a higher level simply replaces the lower one — the lower entry is not written at
all, so there is no reliance on last-wins duplicate-key behaviour.

Because no key is ever written twice, the *physical* order of the generated file carries no meaning
and is instead chosen for safety: the reserved variables are written first, then the operator's, then
your repository's block last. Your `.env` lines are copied through verbatim, so Watchtower is not the
authority on where one of your quoted values ends — putting them last means a value that runs away
can only affect the rest of your own variables, never the injected `WATCHTOWER_*` lines.

An entry in the repository `.env` whose opening quote is never closed is also **dropped**, and the
deploy output says so (`Warning: dropped malformed .env entry 'MOTD' (unterminated quote)`); parsing
continues with the next line, so only that one entry is lost.

The generated file always contains `WATCHTOWER_APP_TOKEN`, so on Linux it is created `0600` and
deleted at the end of the deploy. The compose override file carries the token too and is handled
exactly the same way: both are temporary files sitting beside the deploy's clone directory rather
than inside it, and each is deleted individually when the deploy ends.

### Interpolation still works, if you want to place them yourself

`--env-file` variables are available for **interpolation in the compose file**; on their own they are
not placed inside containers, which is why the override file exists. Writing the passthrough yourself
is therefore no longer required — but it is still fully supported, and it is how you take explicit
control of where a variable lands:

```yaml
services:
  app:
    image: ghcr.io/example/app:latest
    environment:
      - "WATCHTOWER_APP_TOKEN=${WATCHTOWER_APP_TOKEN}"
      - "WATCHTOWER_STACK_ID=${WATCHTOWER_STACK_ID}"
      - "WATCHTOWER_URL=${WATCHTOWER_URL:-}"
```

The `:-` default on `WATCHTOWER_URL` keeps `docker compose` quiet when no public base URL is
configured.

You get the **identical value** either way, so the two mechanisms cannot disagree about what the
token is. Where they do overlap, the override wins per key: it is merged last, so for a service
Watchtower injects into, the injected value is the one in the container. That holds whichever form
your `environment:` uses — compose normalises the list form (`- "KEY=value"`) and the map form
(`KEY: value`) to the same thing and merges them key by key, so only the keys Watchtower injects are
affected and your other entries survive untouched. `environment:` also outranks `env_file:`, so an
injected key is not shadowed by a file your service loads. Passthrough is the way to
put the token into a service the default rules would skip — a plain multi-service stack, say — though
labelling that service `watchtower.inject-token: "true"` does the same job in one line.

Operator-defined stack variables are unaffected by any of this: they reach containers the classic
way, through the env file and your own `environment:` entries.

## Endpoints

All responses are JSON except `/logs`, which is a Server-Sent-Event stream.

### `GET /api/app/self`

Who am I?

```json
{ "stackId": 7, "name": "billing-prod", "tenantSlug": null }
```

### `GET /api/app/status`

Deployment status plus live container state.

```json
{
  "lastDeployStatus": "success",
  "lastDeployedAt": "2026-08-08T19:02:11.421+00:00",
  "lastDeployedCommit": "9f1c2ab7d4e5…",
  "activeDeploy": null,
  "services": [
    {
      "service": "app",
      "containerId": "6f0b1c…",
      "state": "running",
      "status": "Up 12 minutes",
      "image": "ghcr.io/example/app:latest"
    }
  ]
}
```

`activeDeploy` is `{ "id": 42, "status": "running", "startedAt": "…" }` while a deploy is queued or
running, and `null` otherwise. Every status value in this API comes from one vocabulary:
`queued`, `running`, `success`, `failed`.

### `GET /api/app/deployments?limit=`

Recent deploys, newest first. `limit` defaults to `20` and is capped at `100`.

```json
{
  "deployments": [
    {
      "id": 42,
      "status": "success",
      "triggeredBy": "webhook",
      "startedAt": "2026-08-08T19:01:48.002+00:00",
      "finishedAt": "2026-08-08T19:02:11.421+00:00"
    }
  ]
}
```

The captured command output of a deploy is **never** returned — it is produced with git and registry
credentials in scope. Operators can still read it in the UI.

### `GET /api/app/version`

What am I running?

```json
{
  "commit": "9f1c2ab7d4e5…",
  "deployedAt": "2026-08-08T19:02:11.421+00:00",
  "services": [
    {
      "service": "app",
      "image": "ghcr.io/example/app:latest",
      "imageId": "sha256:1c2d3e…",
      "digest": "sha256:aa11bb22…"
    }
  ]
}
```

`commit` is the SHA of the last successful deploy. `imageId` and `digest` are read live from Docker;
either may be `null` if the image can no longer be inspected (e.g. it was pruned).

### `GET /api/app/logs?service=&tail=&follow=`

Container logs as Server-Sent Events, one `data:` frame per line, terminated by an `event: done`
frame.

| Query | Default | Notes |
| --- | --- | --- |
| `service` | — | `com.docker.compose.service` name. Required when the stack exposes more than one service. |
| `tail` | `100` | Clamped to `1…5000`. Applies **per container** — a service with 3 replicas replays up to `3 × tail` lines. |
| `follow` | `false` | `true` keeps the stream open and follows new output. |

If the stack exposes several **services** and no `service` was given, the request is rejected rather
than guessed at (`400`):

```json
{
  "error": "This stack has multiple services; specify ?service=<name>.",
  "services": ["app", "worker"]
}
```

If nothing matches — the stack is not running, or the named service does not exist — the response is
`404` with `{ "error": "…" }`.

**Replicas.** A service scaled to several containers is *not* ambiguous. All of its replicas are
merged into the one stream, and every line is prefixed with the emitting container's 12-character
short id so you can tell them apart:

```
data: 6f0b1c2d3e4f | starting worker loop
data: 9a8b7c6d5e4f | starting worker loop
```

A single matching container streams unprefixed. Interleaving follows arrival order, not timestamps —
lines from different replicas are not globally ordered.

### When something fails mid-stream

The response has already sent its headers by then, so it cannot change status code. Failures are
reported in band as `error` frames, and the frame always names the container it came from:

```
event: error
data: 9a8b7c6d5e4f | log stream failed (the Docker daemon is unreachable)
```

**One replica failing does not end the stream.** That container emits its error frame and stops; the
surviving replicas keep streaming. The response ends only when *every* container has finished — at
which point you get the usual terminator — or when you disconnect:

```
event: done
data:
```

So a well-behaved client treats `error` as "this log source is gone" and `done` as "the stream is
over", and should expect zero or more `error` frames before `done`.

### `GET /api/app/tenants/accessible`

Which sibling tenants may **the user currently visiting you** open? This is the tenant switcher: you
are a tenant of a template, the person in front of you may have access to some of your siblings, and
this endpoint tells you which — so your UI can render the menu.

It is the one place the App API answers about stacks other than yourself, and it does so only for a
user you can *prove* is there. That proof is the [central-auth](central-auth/README.md) identity
assertion, so this endpoint takes **two** headers:

| Header | Value |
| --- | --- |
| `Authorization` | `Bearer wtapp_…` — your own App API token, with the usual `401`/`403` meanings. |
| `X-Watchtower-Jwt` | The assertion from the request **you are currently serving**, forwarded verbatim. |

A tenant needs no compose changes to hold up its end of this: the variables are injected into the
template's target service directly (see
[injected environment variables](#injected-environment-variables)), so a switcher works in a template
whose compose file never mentions Watchtower.

```json
{
  "tenants": [
    { "slug": "customer4", "domain": "customer4.example.com", "current": true },
    { "slug": "customer7", "domain": "customer7.example.com", "current": false }
  ]
}
```

Tenants are sorted by `slug` ascending. Only tenants the user may actually reach are listed: a
`Public` or `Authenticated` sibling always, a `Restricted` one only when that user holds a grant on
it. A sibling with no primary route is omitted, since there is nothing to switch to. Deliberately
absent: stack ids, deploy status, timestamps — this payload is rendered to end users, so it carries
nothing operational.

**`current` marks the calling stack when it appears** — and your own row is filtered exactly like
any other, so it is **absent from the list if the visitor cannot reach your own primary domain**.
That is not a hypothetical: a `Restricted` primary route whose visitor arrived through a different
domain of yours, or a stack with no primary route at all, both produce a list with no `current: true`
entry. Do not assume one exists; render the switcher from the list you got, and fall back to what
you already know about yourself.

| Situation | Response |
| --- | --- |
| Your stack is not a tenant of a template | `404` `{"error":"This stack is not a tenant of a template."}` |
| Central auth is switched off on this Watchtower | `404` |
| The assertion is missing, expired, tampered with, minted for another domain, or names a disabled user | `401` `{"error":"Missing or invalid user assertion."}` |

**One message covers every assertion failure**, on purpose: a caller must not be able to tell "this
token is expired" from "this token is not for you" from "that account is disabled". If you are
debugging an integration, check the obvious three first — that you forwarded the header unchanged,
that the request really came through the proxy, and that your clock is right.

**Why the `aud` matters.** The assertion Watchtower mints is bound to the domain it was issued for
(`aud` = the app's domain), and this endpoint accepts it only if that audience is one of **your own
route domains**. So you can ask about a user who is visiting *you*, and nobody can ask about a user
who is visiting *them*: an assertion collected on another app's domain is a `401` here, no matter
how valid it is elsewhere. That binding is what keeps the endpoint from becoming a way to probe
which tenants an arbitrary user can reach — see
[ADR-0011](decisions/0011-user-scoped-tenant-discovery.md).

`404` when central auth is off is the honest answer rather than a courtesy: with no proxy issuing
assertions there is no user to prove, so there is no question to answer. Products whose routes are
`Public` — they run their own login — never receive an assertion either, and should keep their own
user-to-tenant mapping.

## Examples

Using the variables the application already has in its environment:

```bash
# Am I up to date?
curl -sS -H "Authorization: Bearer $WATCHTOWER_APP_TOKEN" \
  "$WATCHTOWER_URL/api/app/version"

# Is a deploy in flight?
curl -sS -H "Authorization: Bearer $WATCHTOWER_APP_TOKEN" \
  "$WATCHTOWER_URL/api/app/status"

# Last 5 deploys
curl -sS -H "Authorization: Bearer $WATCHTOWER_APP_TOKEN" \
  "$WATCHTOWER_URL/api/app/deployments?limit=5"

# Last 200 log lines of the "worker" service, then stop
curl -sS -N -H "Authorization: Bearer $WATCHTOWER_APP_TOKEN" \
  "$WATCHTOWER_URL/api/app/logs?service=worker&tail=200"

# Follow the log stream
curl -sS -N -H "Authorization: Bearer $WATCHTOWER_APP_TOKEN" \
  "$WATCHTOWER_URL/api/app/logs?service=worker&follow=true"

# Tenant switcher: where else may THIS visitor go?
# $VISITOR_JWT is the X-Watchtower-Jwt of the request you are serving — not a stored value.
curl -sS -H "Authorization: Bearer $WATCHTOWER_APP_TOKEN" \
  -H "X-Watchtower-Jwt: $VISITOR_JWT" \
  "$WATCHTOWER_URL/api/app/tenants/accessible"
```

Consuming the stream from JavaScript — note that `EventSource` cannot send an `Authorization`
header, so use `fetch` when you need one:

```js
const response = await fetch(
  `${process.env.WATCHTOWER_URL}/api/app/logs?service=app&tail=200&follow=true`,
  { headers: { Authorization: `Bearer ${process.env.WATCHTOWER_APP_TOKEN}` } },
);

const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
for (let { value, done } = await reader.read(); !done; { value, done } = await reader.read()) {
  for (const frame of value.split("\n\n")) {
    if (frame.startsWith("event: done")) return;          // every container finished
    if (frame.startsWith("event: error")) {               // one log source died; others continue
      console.warn(frame.split("\n")[1]?.slice(6));
      continue;
    }
    if (frame.startsWith("data: ")) console.log(frame.slice(6));
  }
}
```

## Exposing it

Watchtower has no built-in authentication and is normally placed behind an authenticating reverse
proxy. The App API is one of the few surfaces designed to be reachable by callers that cannot pass
that proxy's login — like the deploy webhook, it carries its own per-stack bearer token. If your
proxy fronts Watchtower, allow `/api/app/*` through unauthenticated (the bearer token is the gate),
or route the applications to Watchtower over an internal network instead.

There is deliberately **no rate limiting** on these routes today; put one in the proxy if the API is
reachable from the public internet.
