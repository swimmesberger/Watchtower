# ADR-0008: Deployed applications query themselves through a token-authenticated REST API

- Status: Accepted
- Date: 2026-08-08
- Related: [ADR-0003](0003-jsonrpc-primary-transport.md) (why this is not a JSON-RPC handler).

## Context

Applications Watchtower deploys have no way to learn anything about their own deployment. An app that
wants to render a "running version 9f1c2ab, deployed 12 minutes ago" badge, expose a deployment-aware
health page, or wait for an in-flight redeploy has to be told out-of-band — or be handed access to
Watchtower's admin API, which would let it read and mutate *every* stack.

The existing surfaces don't fit. The JSON-RPC API (`POST /rpc`) is the operator/UI API: unauthenticated
in-process and fronted by a reverse proxy the application cannot log into, and every method is
stack-agnostic. The deploy webhook is a write trigger, not a read surface.

What an application legitimately needs is narrow and self-referential: *its* status, *its* deployed
version, *its* deploy history, *its* logs.

## Decision

**Add a public App API: a small REST surface under `/api/app/*`, authenticated by a per-stack bearer
token that Watchtower injects into the stack's environment at every deploy.**

- **Identity is the token.** Each stack owns an `app_api_token` (`wtapp_` + 32 random bytes,
  base64url, uniquely indexed) and an `app_api_enabled` flag. The token is minted at stack creation,
  or lazily on the next deploy for pre-existing stacks.
- **The token is delivered by the deploy pipeline.** Alongside `WATCHTOWER_STACK_ID` and the optional
  `WATCHTOWER_URL`, it is written into the temp `.env` handed to `docker compose`, after the
  operator's variables so injected values always win.
- **Stored in plaintext**, consistent with `Stack.WebhookToken` and `Credential.Token`. This is
  forced by the mechanism rather than chosen for convenience: Watchtower must re-inject the *value*
  at every deploy, so a one-way hash would make it unrecoverable and turn every deploy into a
  rotation. The blast radius of a leaked token is one stack's own read-only metadata.
- **401 vs 403 are distinct.** A missing, malformed or unknown token is `401` — it says nothing about
  which stacks exist. A valid token whose stack has the API switched off is `403`, because the caller
  is already proven to be that stack and a `401` would send it into a pointless credential hunt.
- **The caller never names a container.** Every Docker lookup resolves from the authenticated stack's
  `com.docker.compose.project` label. Container ids appear in responses (they are the caller's own)
  but are never accepted as input.
- **Plain minimal-API, not a `[Handler]`,** per ADR-0003: this is externally facing with its own auth
  semantics, and the log endpoint is a stream. Resolution and query logic still lives in an
  Application-project service (`AppApiService`); the host endpoints only translate.
- **Operators stay in control** through two JSON-RPC methods, `stacks.getAppApi` and
  `stacks.setAppApi` (toggle, rotate).

## Consequences

- **Applications become deployment-aware without privilege.** A token grants read-only access to one
  stack's own metadata and nothing else. Tenant instances of a template each get their own token, so
  tenants are isolated from one another by construction.
- **A deliberate list of things the API never returns:** deploy output (produced with git and
  registry credentials in scope), environment variable values, credentials, and any other stack's
  data. This list is the security contract; new endpoints must preserve it.
- **Rotation is eventually consistent.** A rotated token only reaches the running containers at their
  next deploy; until then they present the old value and get `401`. This is inherent to delivering
  the credential through the deploy pipeline, and is documented rather than engineered around.
- **A new externally-reachable surface.** Operators fronting Watchtower with an authenticating proxy
  must allow `/api/app/*` through (the bearer token is the gate) or route applications internally.
- **No rate limiting.** Consistent with the webhook, throttling is left to the reverse proxy. If
  these routes face the public internet, that becomes the operator's job.

### Rejected alternatives

- **Hash the token like a password.** Incompatible with re-injecting it at every deploy; would force
  a rotation per deploy and still leave the plaintext in the container's environment.
- **Expose it as JSON-RPC methods.** Would couple an externally-authenticated, self-scoped surface to
  the operator schema and the generated frontend client, and JSON-RPC has no room for the log stream
  (ADR-0003).
- **Let the caller pass a container id.** Turns every endpoint into an authorization decision about
  someone else's container. Resolving from the stack's own compose project label makes the isolation
  structural instead of enforced case by case.
- **Reuse the existing webhook token.** Conflates a write trigger with a read credential; disabling
  the webhook would silently disable status reads, and rotating one would rotate the other.
