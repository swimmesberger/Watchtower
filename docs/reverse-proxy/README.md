# Built-in reverse proxy

Watchtower can terminate TLS and route public domains to services inside your stacks, so every
container stays internal to Docker and only the proxy is exposed. It uses **Caddy** for automatic
HTTPS.

The feature is **opt-in**. When it is off, none of the behavior below happens.

- Design & rationale: [implementation-plan.md](implementation-plan.md)
- Framework notes: [elarion-framework-notes.md](elarion-framework-notes.md)

## Enabling it

Set two environment variables and make sure host ports 80 and 443 are free:

```yaml
environment:
  WATCHTOWER__PROXY__ENABLED: "true"
  WATCHTOWER__PROXY__ADMINEMAIL: you@example.com   # recommended, for Let's Encrypt notices
  # WATCHTOWER__PROXY__CADDYIMAGE: "caddy:2"        # optional override, defaults to caddy:2
```

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

## Multi-tenancy

Use the **Templates** UI (`/templates`) to run the same stack once per tenant, each on its own
subdomain (`{tenant}.example.com`), fully isolated (own containers, network, and volumes). Adding a
tenant creates an isolated stack, merges the template's base env with per-tenant overrides, creates the
managed route, and deploys.

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
- **Route status is indicative.** Live certificate-state readback from Caddy is a planned follow-up; a
  route may show `pending` until it is refreshed.
