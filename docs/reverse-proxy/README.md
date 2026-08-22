# Reverse proxy

Watchtower can put a public domain in front of a service inside one of your stacks, so every
application container stays internal to Docker and only the ingress is exposed. **Three providers**
serve the same route table; pick one and the rest of the feature — routing, multi-tenancy, access
control — works the same way.

The feature is **opt-in**. While it is off, routes are stored and nothing is served.

| Provider | What it is | Guide |
| --- | --- | --- |
| **`yarp`** (default) | Watchtower terminates 80/443 in its own process and issues its own certificates over ACME. No second container. | [yarp.md](yarp.md) |
| `caddy` *(deprecated)* | A sibling Caddy container Watchtower manages, holding the host's ports 80/443. Kept for existing installs. | [caddy.md](caddy.md) |
| `cloudflare` | A Cloudflare Tunnel: outbound only, no open ports, TLS at Cloudflare's edge, access gated by Zero Trust. | [cloudflare.md](cloudflare.md) |

Background: [ADR-0015](../decisions/0015-proxy-provider-abstraction.md) (the provider seam) and
[ADR-0020](../decisions/0020-in-process-yarp-proxy.md) (the in-process provider and the default flip).

## The route table is the source of truth

A **route** is a domain plus a target: a compose service in one of your stacks and a container port.
You add them in the **Routes** UI (`/routes`); every provider is a projection of that one table, so
switching providers does not mean re-entering anything. Each route also carries its **access mode**
(Public / Authenticated / Restricted) and, for the two certificate-issuing providers, whether it is
served over TLS.

Route status (`Pending`, `Awaiting DNS`, `Active`, `Error`) reports the certificate state, and how much
that is worth depends on the provider. Under the **built-in provider it is authoritative**: Watchtower
issues the certificates itself, so the status, the expiry and the last error all come from its own
certificate store, and the Routes page gains a **Certificates** card listing them per host with a
"Renew now" button. Under **Caddy** it is only indicative — Caddy owns the certificates and does not
report their state back. Under **Cloudflare** TLS terminates at the edge, so the per-route TLS flag
controls nothing.

## Shared network topology

Whichever provider is active, the wiring below it is the same and is created automatically — you do
not declare networks anywhere:

| Network | Members | Purpose |
| --- | --- | --- |
| `watchtower-ingress-{stackId}` | The active proxy + that stack's routed containers | Ingress traffic only; **one per stack**, so tenants cannot reach each other at L2. |
| `watchtower-control` | Caddy + Watchtower | *Caddy only.* Its admin API and the on-demand-TLS callback, off the public path. The other two providers have no control plane. |

Each routed container joins its stack's ingress network under the stable alias
`{project}-{service}`, and the proxy reaches it as `{project}-{service}:{port}`. Your services never
need `ports:` in their own compose files.

Note what this means for the built-in provider: **Watchtower's own container joins every ingress
network**, because it *is* the proxy. That is a deliberate exposure change from the Caddy topology —
see the consequences section of [ADR-0020](../decisions/0020-in-process-yarp-proxy.md).

## Choosing a provider

```yaml
environment:
  WATCHTOWER__PROXY__ENABLED: "true"
  WATCHTOWER__PROXY__PROVIDER: yarp   # yarp (default) | caddy (deprecated) | cloudflare
```

Or pick it under **Settings → Reverse proxy**, which applies immediately: switching tears the old
provider's data plane down and reconciles the new one, with no restart. Teardown removes only that
provider's own plane — Caddy keeps its certificates, Cloudflare keeps the tunnel and its DNS records.

Environment variables win over the UI ([ADR-0014](../decisions/0014-env-wins-runtime-settings.md)): a
setting supplied that way shows as pinned and read-only on the Settings page until the variable is
removed.

**Upgrading from before ADR-0020:** an instance that has routes and never named a provider is pinned
to `caddy` once, at the first start after the upgrade — it keeps running exactly as it did, and the
change is logged and recorded in the audit trail. Fresh installations get the built-in provider, and
an internal marker written on that first start makes sure they are never pinned later, once they have
routes of their own. Moving an existing install over is a deliberate act: see
[caddy.md](caddy.md) for both halves of this.

## Multi-tenancy

Use the **Templates** UI (`/templates`) to run the same stack once per tenant, each on its own
subdomain (`{tenant}.example.com`), fully isolated — own containers, network and volumes. Adding a
tenant creates the isolated stack, merges the template's base environment with per-tenant overrides,
creates the managed route, and deploys. This is provider-independent.

## Access control

With authentication enabled, a route can be gated centrally: Public, any authenticated user, or
selected users and groups — with a signed identity forwarded to the application. The decision core is
shared by the providers that run it in front of your apps, so `yarp` and `caddy` reach identical
verdicts; under `cloudflare`, access belongs to Zero Trust instead. See
[docs/central-auth/README.md](../central-auth/README.md).

## Also here

- [implementation-plan.md](implementation-plan.md) — the original design and rationale for the route
  table and the Caddy engine.
- [elarion-framework-notes.md](elarion-framework-notes.md) — framework notes gathered while building it.
