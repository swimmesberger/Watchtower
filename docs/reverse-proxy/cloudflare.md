# Cloudflare Tunnel provider

Serve your routes through a [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/)
instead of the built-in Caddy proxy ([ADR-0015](../decisions/0015-proxy-provider-abstraction.md)):
no open host ports, TLS terminated at Cloudflare's edge, and optional Zero Trust access control in
front of every hostname. The `routes` table stays the single source of truth — Watchtower projects it
into the tunnel's **public hostnames** (ingress rules) and the matching **proxied DNS records**, the
same things you would otherwise click together under
*Zero Trust → Networks → Tunnels → Public hostname* and *Access → Applications*.

## Setup

1. Create an API token with **Cloudflare Tunnel: Edit**, **DNS: Edit**, and — for Access-protected
   routes — **Access: Apps and Policies: Edit** (scoped to the account and the zone your domains
   live under).
2. In **Settings → Reverse proxy**, select the **Cloudflare Tunnel** provider and fill in the account
   id, zone id, API token (validated on save), and a tunnel name (default `watchtower`).
3. Choose who runs `cloudflared`:
   - **Managed (default):** Watchtower finds or creates the remotely-managed tunnel, fetches its run
     token, and supervises a `watchtower-cloudflared` container over the Docker socket — the same way
     it manages the Caddy container. Zero manual steps.
   - **Unmanaged:** you already run cloudflared (as a container, a service, or on another machine).
     Watchtower only manages the tunnel's remote configuration and DNS. Set the tunnel name to your
     existing tunnel's name; Watchtower will not create tunnels in this mode. If your cloudflared
     runs as a container on the same Docker host, enter its name and Watchtower connects it to the
     per-stack ingress networks so the generated service URLs resolve; otherwise reaching the
     services is your setup's responsibility.
4. Enable the proxy. Every change applies at runtime — no restart.

## What Watchtower reconciles

On startup, on every route change/deploy, and on every settings change:

- one ingress rule per route — `https://{domain}` → `http://{project}-{service}:{containerPort}`
  (plain HTTP inside the private per-stack ingress network; the public leg is TLS at the edge),
  terminated by the mandatory `http_status:404` catch-all;
- one **proxied CNAME** per route domain → `{tunnelId}.cfargotunnel.com`;
- the cloudflared container (managed mode) and its ingress-network memberships;
- one **Zero Trust Access application** (`self_hosted`, named `watchtower: {domain}`) per protected
  route, with a single Watchtower-owned allow policy:
  - **Authenticated** routes admit the instance-wide allow sources configured on the Settings page:
    *allowed emails*, *email domains*, **Access group ids** (the natural fit when your allow-list
    already lives in an Access group — e.g. your Entra ID users), and/or **reusable Access policy
    ids** (your dashboard-maintained default policy, attached on the app rather than recreated);
  - **Restricted** routes admit exactly the emails behind the route's grants — granted users plus
    members of granted groups (accounts without an email address cannot be matched by Cloudflare and
    are effectively excluded);
  - a protected route whose allow-list comes out **empty is skipped with a warning** rather than
    published as a deny-all app, and any existing app is left untouched — a silent total lockout is
    the worse failure;
  - a route flipped back to **Public** gets its Watchtower-created app deleted. Only apps carrying
    the `watchtower: ` name prefix are ever deleted; dashboard-made apps are never touched.

Disabling the proxy — or switching back to Caddy — stops and removes only the managed cloudflared
container. **The tunnel and the DNS records are kept**: deleting public DNS you may still want is not
a toggle's job, and re-enabling reuses both.

## Pre-existing tunnel hostnames (merge & import)

The configuration push **merges, never replaces**: public hostnames you configured in the Cloudflare
dashboard (before or beside Watchtower) whose hostname is not in Watchtower's route table are
preserved verbatim — original order and `path` filters included — on every reconcile. Pointing
Watchtower at a tunnel you already use is therefore non-destructive.

The Routes page surfaces those dashboard-made hostnames in a **"Found in Cloudflare"** card when the
cloudflare provider is active. *Import* prefills the new-route form — with a heuristic
stack/service/port suggestion when the service URL follows Watchtower's own
`http://{project}-{service}:{port}` alias convention; anything else (IPs, localhost ports) you map to
a stack service by hand. Once imported, the hostname is owned by the route table: it gains access
control, per-stack networking, and cleanup on stack removal, and the route row's target replaces the
dashboard rule on the next push.

## Audit trail

Every write the provider performs against your Cloudflare account is recorded in Watchtower's
general audit trail (category `proxy.cloudflare`). The global **Audit** page (sidebar, admin-only)
shows every category; **Routes → Audit** embeds the proxy-scoped slice of the same trail: tunnel creation,
ingress configuration pushes (with rule counts and how many foreign rules were preserved), DNS
record creates/updates, and Access app/policy changes — success or failure, with Cloudflare's error
message on failure. Reads are not logged, and no-op reconciles (nothing changed) produce no entries.
Retention is bounded (newest 2000 events kept). The trail is admin-only and survives the deletion of
the routes/stacks it mentions. The same `audit.listEvents` surface is category-filterable, so future
planes (deploys, settings changes) land in the same log.

## LAN port routes work alongside the tunnel

A **port route** — a stack service on a dedicated TLS port of Watchtower's own, with a certificate from
Watchtower's own CA — is not this provider's business in either direction, and is not refused by it. Its
listener is inside the Watchtower container, so `app.example.com` can be served through the tunnel while
`https://nas.lan:9001` is served on the LAN at the same time, from the same instance.

What joins which network: **cloudflared** joins the ingress network of every stack with a *domain* route,
and **Watchtower's own container** joins the ingress network of every stack with a *port* route. A stack
with both gets both, on the one network it already had.

The full walkthrough — LAN names, publishing the host port, importing the root — is in
[README.md → Port routes](README.md#port-routes-https-on-a-lan-with-any-provider).

## Limitations

- **Watchtower routes are not served by this provider.** A route whose target is Watchtower itself
  ([ADR-0023](../decisions/0023-login-hosts-are-watchtower-self-routes.md)) is skipped by the tunnel
  projection and its row is set to `Error` saying so. An ingress rule pointing at Watchtower would
  publish the management plane through the tunnel with nothing in front of it — which is exactly what
  Cloudflare Access exists to do properly, so expose Watchtower through the Cloudflare dashboard and
  gate it there. The row is still worth keeping: it is where the realm's login address is written down,
  and that is what its protected apps redirect to.
- **Single zone:** all route domains must live under the configured zone id. A domain outside it
  fails its DNS upsert (logged, best-effort) while the rest proceed.
- **Access control:** Watchtower's forward-auth (central-auth) does not run in front of tunneled
  routes — protection is Cloudflare Access, projected from `Route.AccessMode` as described above.
  Apps behind a protected route see Cloudflare's `Cf-Access-Jwt-Assertion`, not the
  `X-Watchtower-*` identity headers, and the identity is whoever passed the Access policy (your
  Cloudflare One login methods), not a Watchtower session. For apps written against Cloudflare's
  headers, integrated auth offers the matching **Cloudflare identity-forwarding mode** on the route
  (see the central-auth docs), so the same stack code runs behind either — and with the
  **Zero Trust team** configured, deploys inject `WATCHTOWER_AUTH_JWKS_URL` (the team's
  `/cdn-cgi/access/certs` here, Watchtower's `/api/auth/jwks` under integrated auth), so a
  JWT-verifying app reads its JWKS location from the environment and the edge switch needs no app
  configuration at all — see docs/public-app-api.md.
- **TLS mode:** upstream connections from cloudflared to your services are plain HTTP on the private
  ingress network, like Caddy's; the route's `TlsEnabled` flag is not consulted by this provider.
