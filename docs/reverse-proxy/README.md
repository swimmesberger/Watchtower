# Reverse proxy

Watchtower can put a public domain in front of a service inside one of your stacks, so every
application container stays internal to Docker and only the ingress is exposed. **Three providers**
serve the same route table; pick one and the rest of the feature — routing, multi-tenancy, access
control — works the same way.

The feature is **opt-in**. While it is off, routes are stored and nothing is served.

| Provider | What it is | Guide |
| --- | --- | --- |
| **`yarp`** (default) | Watchtower terminates 80/443 in its own process and issues its own certificates over ACME. No second container. Ingress is on its own container ports (`80:8081`, `443:8443` by default — a reverse-proxy setting, bound only while this provider is enabled), separate from the management endpoint on 8080. | [yarp.md](yarp.md) |
| `caddy` *(deprecated)* | A sibling Caddy container Watchtower manages, holding the host's ports 80/443. Kept for existing installs. | [caddy.md](caddy.md) |
| `cloudflare` | A Cloudflare Tunnel: outbound only, no open ports, TLS at Cloudflare's edge, access gated by Zero Trust. | [cloudflare.md](cloudflare.md) |

One capability is not shared, because it is a listener on Watchtower's own host and nothing else can
lend one:

| Capability | `yarp` | `caddy` | `cloudflare` |
| --- | --- | --- | --- |
| **Port routes** — HTTPS on a LAN address with no domain (`https://nas.lan:9001`), certified by a CA Watchtower generates for itself | ✔ | — | — |

Under the other two providers such a route is stored but reports `Error` saying so. See
[yarp.md](yarp.md) and [ADR-0033](../decisions/0033-port-routes-and-internal-ca.md).

Background: [ADR-0015](../decisions/0015-proxy-provider-abstraction.md) (the provider seam) and
[ADR-0022](../decisions/0022-in-process-yarp-proxy.md) (the in-process provider and the default flip).

## The route table is the source of truth

A **route** is an address plus a target. The address is a **domain** — the usual case — or, under the
built-in provider only, a **port** of its own on this host, for a LAN deployment that has no domain to
put in front of anything ([ADR-0033](../decisions/0033-port-routes-and-internal-ca.md)). The target is
either a **stack service** — a compose service in one of your stacks and a container port — or
**Watchtower itself**, which is how this instance's own UI and login pages get their hostnames
([ADR-0023](../decisions/0023-login-hosts-are-watchtower-self-routes.md), and "Exposing Watchtower
itself" below). You add them in the **Routes** UI (`/routes`); every provider is a projection of that one
table, so switching providers does not mean re-entering anything. Each route also carries its **access
mode** (Public / Authenticated / Restricted) and, for the two certificate-issuing providers, whether it
is served over TLS — a port route is always public and always TLS, since it has no hostname a login
redirect could return a visitor to.

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
see the consequences section of [ADR-0022](../decisions/0022-in-process-yarp-proxy.md).

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

**Upgrading from before ADR-0022:** an instance that has routes and never named a provider is pinned
to `caddy` once, at the first start after the upgrade — it keeps running exactly as it did, and the
change is logged and recorded in the audit trail. Fresh installations get the built-in provider, and
an internal marker written on that first start makes sure they are never pinned later, once they have
routes of their own. Moving an existing install over is a deliberate act: see
[caddy.md](caddy.md) for both halves of this.

## Exposing Watchtower itself

Watchtower's own UI needs a hostname too — and so does every realm's login page, because a protected
application redirects anonymous visitors to `https://{loginHost}/login`. Both are the same thing: a
route whose target is **Watchtower (this instance)** rather than a stack service. Create one in the
Routes UI, pick the realm it serves, and tick **Use as this realm's login host** if its visitors should
be redirected there.

Such a route has no stack, no service and no port, and it is always **Public**: Watchtower authenticates
its own visitors natively, so route access control does not apply and the database refuses to store a
gated one. With authentication *disabled*, publishing one exposes the management UI to anyone who can
reach the domain — turn authentication on first.

A worked example. One instance, authentication on, the `yarp` provider, the management UI at
`watchtower.example.com`, and a customer realm `acme`:

| id | domain | target | stack / service:port | realm | login route? | access | served as |
|---|---|---|---|---|---|---|---|
| 1 | `watchtower.example.com` | Watchtower | — | system | ✔ (system) | Public (enforced) | in-process: management UI + operator login |
| 2 | `app.example.com` | Service | `myapp` / `web:3000` | system (via stack) | — | Authenticated | forwarded after forward-auth |
| 3 | `login.acme.com` | Watchtower | — | acme | ✔ (acme) | Public (enforced) | in-process: acme login page + "your applications" portal |
| 4 | `crm.acme.com` | Service | `acme-crm` / `web:8080` | acme (via template) | — | Restricted | forwarded after forward-auth |
| 5 | `admin.example.com` | Watchtower | — | system | — | Public | in-process: a second UI hostname, not used for redirects |

`realms`: system (login route → 1), acme (login route → 3). Settings: `Proxy:Enabled=true`,
`Provider=yarp`, `Auth:Enabled=true`, `Auth:Host` **empty**. Ports: `127.0.0.1:8080:8080` (management,
private), `80:8081`, `443:8443` — the ingress container ports being the yarp defaults.

What each request does:

- `https://watchtower.example.com/` → ingress → row 1 is served by Watchtower → the UI, `__wt_sso` login.
- `https://app.example.com/` while anonymous → row 2 is protected → the access check resolves realm
  *system* → 302 to `https://watchtower.example.com/login?redirect_uri=…` → after signing in,
  `/.watchtower/callback` on `app.example.com` mints the per-app `__wt_access` cookie → forwarded to
  `myapp-web:3000` with `X-Watchtower-Jwt`.
- `https://crm.acme.com/` while anonymous → realm *acme* → 302 to `https://login.acme.com/login`.
- `http://watchtower.example.com/` on port 80 → 302 to https.
- `http://<public-ip>/` → 404. An unknown host on ingress is a stranger and gets nothing.
- `http://nas.lan:8080/` → the management UI, on the port you bound privately.

All five rows get ACME certificates and report their status on the Routes page. Deleting row 1 is
allowed — Watchtower warns that the system realm then has no login host, so its protected apps answer
anonymous visitors with 401 until another Watchtower route is designated; the UI itself stays reachable
on 8080.

**Behind another proxy.** If something else terminates TLS in front of Watchtower and the built-in
proxy is off, nothing here is served or issued by us — but rows 1 and 3 still supply the redirect
hostnames, which is all the auth path needs. For the operator realm you may instead set
`WATCHTOWER__AUTH__HOST`, which is read **only** when the operator realm has no login route designated.
Prefer a route: a route is served, gets a certificate, reports a status and is audited.

**Upgrading from before ADR-0023:** every realm's stored auth host becomes a Watchtower route during the
migration, and a configured `Auth:Host` becomes the operator realm's on the first start after it.
Neither ever re-points a hostname that already serves an application. The `Auth:Host` half is recorded
in the audit trail as `proxy` / `route.convert`; the migration half is not — it is a schema migration,
and the migration history is its record.

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
