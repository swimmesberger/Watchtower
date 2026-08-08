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

Tokens are stored in plaintext on the stack row, like `WebhookToken` and registry credentials —
Watchtower has to read the value back to re-inject it at every deploy. See
[ADR-0008](decisions/0008-public-app-api.md).

Operators manage the token from the admin JSON-RPC API:

- `stacks.getAppApi` → `{ enabled, token, injectedVarNames }`
- `stacks.setAppApi` → same shape; accepts `{ stackId, enabled?, regenerateToken? }`

Rotating a token takes effect for the running containers only at their **next deploy** — until then
they keep presenting the old value and will receive `401`.

## Injected environment variables

At deploy time Watchtower writes these into the temporary `.env` it passes to
`docker compose --env-file`, **after** the operator's own stack variables. Reserved names always win:
an operator variable using one of these keys is skipped.

| Variable | Value | Always present |
| --- | --- | --- |
| `WATCHTOWER_APP_TOKEN` | The stack's App API bearer token | yes |
| `WATCHTOWER_STACK_ID` | Watchtower's numeric stack id | yes |
| `WATCHTOWER_URL` | Watchtower's public base URL | only when `Watchtower:PublicBaseUrl` is configured |

Set the base URL with `WATCHTOWER__PUBLICBASEURL=https://watchtower.example.com` (or the
`Watchtower:PublicBaseUrl` config key). Without it the variable is simply not injected and the
application must know where Watchtower lives by other means.

### ⚠️ Compose only *interpolates* env-file variables — reference them explicitly

This is the part that trips people up. `--env-file` variables are available for **interpolation in
the compose file**; they are *not* automatically placed inside your containers. Your compose file has
to pass them through:

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
configured. If you omit these lines, the variables exist during the compose run but never reach your
application, and every call will fail with `401`.

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
| `service` | — | `com.docker.compose.service` name. Required when the stack runs more than one container. |
| `tail` | `100` | Clamped to `1…5000`. |
| `follow` | `false` | `true` keeps the stream open and follows new output. |

If the stack has several containers and no `service` was given, the request is rejected rather than
guessed at:

```json
{
  "error": "This stack has multiple services; specify ?service=<name>.",
  "services": ["app", "worker"]
}
```

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
    if (frame.startsWith("event: done")) return;
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
