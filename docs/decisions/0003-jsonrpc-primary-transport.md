# ADR-0003: JSON-RPC is the primary transport; streaming and webhooks stay plain HTTP

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0001](0001-rebuild-on-elarion.md) (the framework that introduces JSON-RPC).

## Context

Elarion maps every `[Handler]` onto a single dispatcher and exposes it over JSON-RPC (`POST /rpc`), with a
schema export that generates a typed TypeScript + Zod client. That is an excellent fit for request/response
operations — the CRUD and actions that make up most of Watchtower's API.

It is a poor fit for two things Watchtower needs:

- **Server-push streaming.** The UI streams live deploy output and container logs as they are produced. A
  JSON-RPC method returns a single `Result<T>`; it has no place for an open, incrementally-flushed stream.
- **Externally-facing, differently-authenticated endpoints.** The deploy webhook is called by external CI,
  not the SPA, and is authorized by a per-stack bearer token (independent of however the UI is fronted). It
  needs raw access to the request/response and its own auth semantics.

## Decision

**Serve the request/response API over JSON-RPC, and keep streaming + externally-facing endpoints as plain
minimal-API HTTP.**

- All 29 CRUD/action operations are handlers over `POST /rpc` (`credentials.*`, `registries.*`, `stacks.*`,
  `containers.*`, `deployments.active`, `system.*`). The frontend calls them through the generated client.
- Plain HTTP endpoints in the host handle the rest:
  - `POST /api/webhooks/stacks/{id}/deploy` — bearer-token deploy trigger (returns 404 when the stack's
    webhook is disabled, so it never reveals stack existence).
  - `GET /api/stacks/events/{eventId}/stream` and `GET /api/containers/{id}/logs` — Server-Sent-Event streams.
  - `GET /health`.

## Consequences

- **The bulk of the API gets a typed, schema-driven client for free**, while the streaming UX and the
  external webhook contract keep the shape each actually needs.
- **The webhook is a stable, documented external surface** independent of the RPC schema — safe to hand to
  CI systems and reverse proxies.
- **Two transports to keep in mind.** Contributors add most operations as handlers, but streaming/external
  concerns are hand-mapped minimal-API routes. The split is small and the boundary is clear (push vs.
  request/response; external vs. UI), so this is a low ongoing cost.

### Rejected alternatives

- **Force streaming into JSON-RPC** (e.g. return a batch, or poll). Loses the live, incremental UX that the
  deploy/log views depend on.
- **Expose the webhook as a JSON-RPC method.** Couples an external, token-authenticated trigger to the RPC
  envelope and schema, and complicates the "don't reveal stack existence" 404 behavior.
