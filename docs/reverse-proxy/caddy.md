# Caddy provider (deprecated)

> **Deprecated since 2026-08-22**, in favour of the built-in in-process provider
> ([yarp.md](yarp.md), [ADR-0020](../decisions/0020-in-process-yarp-proxy.md)). It is kept, supported
> and selectable for installations already running on it — nothing is switched underneath you, and an
> existing instance that never named a provider is pinned to `caddy` once at startup. It does the same
> job as the built-in provider at the cost of a second container, a control network, an admin-API hop
> per route change and an HTTP forward-auth hop per protected request, and its certificate state is
> invisible to Watchtower, which is why route status is only indicative here. Removing it will be its
> own decision record. To move over, set `WATCHTOWER__PROXY__PROVIDER=yarp` (or pick the built-in
> provider under Settings → Reverse proxy), publish `80:8081` and `443:8443` on Watchtower's
> container (its ingress endpoints — 8080 stays the management plane), and remove the `watchtower-caddy` container once the new certificates are issued.

With this provider Watchtower terminates TLS and routes public domains to services inside your stacks
through a sibling **Caddy** container, so every application container stays internal to Docker and
only Caddy is exposed. Caddy handles automatic HTTPS.

The feature is **opt-in**. When it is off, none of the behavior below happens.

## How an existing install stays on Caddy

Before [ADR-0020](../decisions/0020-in-process-yarp-proxy.md) this was the default, so an instance that
enabled the proxy and added routes never had to name a provider. On the **first start after upgrading
past that flip**, Watchtower looks for exactly that shape — routes in the table, no provider stated in
the environment or the settings store — and, finding it, writes `caddy` into the settings store. It
logs a line saying so and records a `proxy` / `config.migrate` row in the audit trail. Nothing changes
about how the instance runs; the provider it was already using simply becomes explicit, visible on the
Settings page and editable there.

That decision is made **once**. Watchtower also stores an internal marker
(`Watchtower:Proxy:ProviderMigrated`) on that first start, whether or not it pinned anything, because a
route table stops being evidence of anything soon afterwards: a fresh installation on the new default
adds routes of its own, and a rule that re-read the table on every start would eventually pull it onto
Caddy. The marker is not a setting — it is not shown in the UI and cannot be set from the environment.

To move a pinned installation onto the built-in provider, see the migration note at the top of this
page.

For what all three providers share — the route table, the ingress-network topology, provider
selection — start at [README.md](README.md).

- Design & rationale: [implementation-plan.md](implementation-plan.md)
- Framework notes: [elarion-framework-notes.md](elarion-framework-notes.md)

## Enabling it

Make sure host ports 80 and 443 are free, then set `WATCHTOWER__PROXY__PROVIDER=caddy` (the default
is the built-in provider since ADR-0020) and either flip **Settings → Reverse proxy** in the UI
(applies immediately, no restart — disabling stops and removes the managed Caddy container while
keeping networks and issued certificates), or pin it via environment variables:

```yaml
environment:
  WATCHTOWER__PROXY__ENABLED: "true"
  WATCHTOWER__PROXY__PROVIDER: caddy                # required since ADR-0020; the default is yarp
  WATCHTOWER__PROXY__ADMINEMAIL: you@example.com    # recommended, for Let's Encrypt notices
  # WATCHTOWER__PROXY__CADDYIMAGE: "caddy:2"        # optional override, defaults to caddy:2
```

Env vars win over the UI ([ADR-0014](../decisions/0014-env-wins-runtime-settings.md)): a setting
supplied this way shows as pinned (read-only) on the Settings page until the variable is removed.

That's the whole setup. **You do not add Caddy to any compose file.** Watchtower already has the
Docker socket and the host docker GID (which it needs anyway), and that is all it requires.

## Watchtower auto-deploys and manages Caddy

You do not run Caddy yourself — Watchtower creates and supervises it over the Docker socket, the same
way it manages the self-update coordinator, but long-lived. On startup (and whenever it reconciles) it:

1. **Pulls** `caddy:2` if the image is missing.
2. **Creates** a container named **`watchtower-caddy`**:
   - publishes host ports **80** and **443** (tcp, plus 443/udp for HTTP/3),
   - mounts two **named volumes** — `caddy_data` (`/data`, certificates & ACME state) and
     `caddy_config` (`/config`, autosaved config),
   - restart policy `unless-stopped`,
   - attached to the private `watchtower-control` network.
3. **Starts** it, connects the routed service containers to their per-stack ingress network, and
   pushes the generated config via Caddy's admin API.

The named volumes and all networks are **created automatically** by the Docker daemon on first use —
you don't declare any of them anywhere. Nothing in `deploy/docker/docker-compose.yml` needs to change
(the comments there only document the opt-in).

### Networks

| Network | Members | Purpose |
| --- | --- | --- |
| `watchtower-control` | Caddy + Watchtower | Admin API (config push) and the on-demand-TLS callback — off the public path. |
| `watchtower-ingress-{stackId}` | Caddy + that stack's routed containers | Ingress traffic only; one per stack, so tenants can't reach each other. |

Only Caddy publishes host ports; your services never need `ports:` in their compose.

## How routing and TLS work

- Add routes in the **Routes** UI (`/routes`): a domain → a compose service + port. Watchtower stores
  the route, joins the target container to the stack's ingress network under a stable alias, and
  reloads Caddy.
- **Managed subdomains** get a certificate issued proactively (HTTP-01). Point the domain's DNS at the
  host first; the built-in DNS preflight helps you check.
- **Customer-owned custom domains** use Caddy's **on-demand TLS**, gated by Watchtower's
  `GET /api/proxy/ask` endpoint, which authorizes a certificate only for domains that exist in the
  route table.
- Config is pushed to Caddy's admin API (`/load`) for a **zero-downtime reload** — no restart, no
  shared config file.

## Operational notes & current limitations

- **Lifecycle is Watchtower's.** On restart it reconciles: a healthy `watchtower-caddy` is reused; a
  stale one is removed and recreated. If you `docker rm` it, Watchtower brings it back on the next
  reconcile.
- **It's a sibling container**, not part of your Watchtower compose project — it appears as a
  standalone `watchtower-caddy` container on the host.
- **Disabling the proxy does not tear Caddy down.** Setting `WATCHTOWER__PROXY__ENABLED=false` stops
  Watchtower from managing/reconciling it, but an already-running `watchtower-caddy` keeps running until
  you remove it manually: `docker rm -f watchtower-caddy`.
- **Caddy image upgrades are not automated.** The image is pulled only when the container is missing.
  To move to a newer Caddy, remove the container so it is recreated from a freshly pulled image.
- **Ports 80/443 must be free.** If you already run a proxy there, leave the feature off and keep your
  own. (A "bring-your-own-Caddy, Watchtower only generates config" mode is not wired up today — the
  config/reload path targets the container Watchtower manages.)
- **Route status is indicative.** Caddy owns the certificates and does not report their state back,
  so a route may show `pending` after it is in fact being served. This is not going to be fixed here
  — the built-in provider issues the certificates itself and reports real state ([yarp.md](yarp.md)).

## Multi-tenancy

Use the **Templates** UI (`/templates`) to run the same stack once per tenant, each on its own
subdomain (`{tenant}.example.com`), fully isolated (own containers, network, and volumes). Adding a
tenant creates an isolated stack, merges the template's base env with per-tenant overrides, creates the
managed route, and deploys. This is provider-independent.
