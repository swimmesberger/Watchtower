# Cloudflare Tunnel provider

Serve your routes through a [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/)
instead of the built-in Caddy proxy ([ADR-0015](../decisions/0015-proxy-provider-abstraction.md)):
no open host ports, TLS terminated at Cloudflare's edge, and optional Zero Trust access control in
front of every hostname. The `routes` table stays the single source of truth — Watchtower projects it
into the tunnel's **public hostnames** (ingress rules) and the matching **proxied DNS records**, the
same things you would otherwise click together under
*Zero Trust → Networks → Tunnels → Public hostname* and *Access → Applications*.

## Setup

1. Create an API token with **Cloudflare Tunnel: Edit**, **DNS: Edit**, **Zone: Read** (so Watchtower
   can discover the zones your routes live under), and **Access: Apps and Policies: Edit** — the last
   one is effectively required now that every new route is protected by default (see below), not just
   for routes you protect deliberately. Scope it to the account and the zones your domains live under.
2. In **Settings → Reverse proxy**, select the **Cloudflare Tunnel** provider and fill in the account
   id, API token (validated on save), and a tunnel name (default `watchtower`). The **zone id is
   optional**, and there are two ways to fill it in:
   - **Leave it blank** and let Watchtower discover your zones from the token. This is the one that
     serves routes across more than one domain, and it needs `Zone:Read`; the save is refused if the
     token cannot list a single zone.
   - **Paste a zone id** as before. Nothing about your setup changes, `Zone:Read` is not required, and
     every route whose domain no discovered zone covers falls back to that zone.
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
- one **proxied CNAME** per route domain → `{tunnelId}.cfargotunnel.com`, written **in the zone whose
  name is the longest suffix of that domain** (falling back to the configured zone id — see
  *Zone discovery* below);
- the cloudflared container (managed mode) and its ingress-network memberships;
- one **Zero Trust Access application** (`self_hosted`, named `watchtower: {domain}`) per protected
  route, with a single Watchtower-owned allow policy:
  - **Authenticated** routes with no **access rules** attached admit the instance-wide allow sources
    configured on the Settings page: *allowed emails*, *email domains*, **Access group ids** (the natural
    fit when your allow-list already lives in an Access group — e.g. your Entra ID users), and/or
    **reusable Access policy ids** (your dashboard-maintained default policy, attached on the app rather
    than recreated). These apply to **every** protected hostname alike, which is what
    [access rules](#access-rules-different-people-per-hostname) exist to vary;
  - **Authenticated** routes **with** access rules attached admit exactly what those rules resolve to,
    and the instance-wide sources do not apply to them at all;
  - **Restricted** routes admit exactly the emails behind the route's grants — granted users plus
    members of granted groups, **of the route's own realm** (accounts without an email address cannot be
    matched by Cloudflare and are effectively excluded, and a grant left behind by a realm change admits
    nobody here just as it admits nobody in process —
    [ADR-0040](../decisions/0040-the-edge-projection-is-authoritative.md));
  - a protected route whose allow-list comes out **empty gets an explicit deny-all app** and its row
    is set to `Error` saying so. Nobody reaches it until you configure an allow source or set the
    route Public. This is deliberate and it reverses what earlier versions did: a lockout tells you
    about itself the moment anyone tries to sign in, while a route that says *Authenticated* on the
    Routes page and is served to everyone tells you nothing
    ([ADR-0035](../decisions/0035-new-routes-are-protected-by-default.md));
  - a route flipped back to **Public** gets its Watchtower-created app deleted. Only apps carrying
    the `watchtower: ` name prefix are ever deleted; dashboard-made apps are never touched.
- a second Access application, `watchtower: {host} (public paths)`, for a protected route that has
  **bypass paths** — the anonymous allow-list you set under *Routes → Access* for webhooks and OAuth
  callbacks. It carries `{host}{path}` for each path with a single `bypass` policy for Everyone, and
  Cloudflare's most-specific-application rule is what makes it win on those paths while the route's own
  app keeps everything else. It is published for a denied-out route too — a webhook has no identity to
  present, and the lockout above is about people. Note that the edge matches by **path segment** while
  Watchtower's own in-process check matches a raw prefix and refuses any path carrying a percent-encoded
  byte or a `..` segment; the edge applies no such guard, so keep bypass prefixes narrow and point them
  at endpoints that authenticate their own callers.

Disabling the proxy — or switching back to Caddy — stops and removes only the managed cloudflared
container. **The tunnel and the DNS records are kept**: deleting public DNS you may still want is not
a toggle's job, and re-enabling reuses both.

### Access rules: different people per hostname

The four settings above are **instance-wide** — every `Authenticated` route in the deployment gets the same
allow-list. When one hostname should admit more people than the others (your own apps for your household,
one shared app for friends too), that is what **access rules** are for
([ADR-0039](../decisions/0039-access-rules-compose.md)).

A rule is a **name** and a list of **clauses**, managed under **Routes → Access rules**. It admits anyone
matching any clause, and a route attaches it by name under **Routes → Access**. The composition you
probably came here for is two rules and two attachments:

```
Access rule "family"    →  Cloudflare reusable policy: family
Access rule "friends"   →  Cloudflare reusable policy: friends

internal.example.com    →  [ family ]
shared.example.com      →  [ family, friends ]
```

`shared.example.com`'s application gets **both** policies attached, in that order; `internal.example.com`
gets only the first. Before rules, both hostnames necessarily got the identical instance-wide list.

**Six clause kinds**, and the two columns matter as much as the list:

| Clause | Cloudflare Access | Built-in proxy (`yarp`) |
| --- | --- | --- |
| **Watchtower group** | member email addresses, resolved at each reconcile | evaluated per request |
| **Watchtower user** | that account's email address | evaluated per request |
| **Email address** | an `email` include | not enforceable |
| **Email domain** | an `email_domain` include | not enforceable |
| **Cloudflare Access group** | an Access-group include | not enforceable |
| **Cloudflare reusable policy** | attached to the app by id, never edited | not enforceable |

The right-hand column is **enforced, not advisory**: attaching a rule the active provider cannot honour is
**refused when you save it**, naming the clause and the provider, rather than being accepted and then
quietly ignored at the next reconcile. The Access dialog greys out those rules so you can see it before
saving, and each rule carries a badge saying where it can be enforced. A rule may still be *created* with
clauses the current provider cannot honour — that is how you stage a move between edges, keeping both
spellings on one rule while the cutover happens.

Two consequences worth knowing:

- **A group's members are flattened at reconcile time** under this provider, so removing somebody from a
  group takes effect on the next reconcile rather than on their next request. Under the built-in proxy it is
  immediate. Same declaration, different revocation latency.
- **The email kinds are edge-only on purpose.** Cloudflare's identity provider verifies an address before
  asserting it; Watchtower does not (an account's email is optional and not unique), so matching a
  Watchtower session on an address would make an unverified field an authorization key.

**Rules replace the instance-wide settings for the route that attaches them** — they do not add to them. So
an internal hostname can be narrower than the default, which "extend" could never express. Attach no rules
and the route keeps the instance-wide list; tick none again and it goes back to it.

**A rule that resolves to nobody locks its route out**, exactly as an empty instance-wide list does
(see below) — with the rule named in the warning and on the route's row, because the remedy is in the rule
rather than on the Settings page.

Rules belong to `Authenticated` routes. **Restricted** means "exactly the users and groups granted on this
route", so attaching a rule to one is refused rather than merged — the two are separate axes, not a
combination.

**Picking a policy by name.** The clause editor lists your account's reusable Access policies so you choose
*friends* rather than pasting a UUID. It asks for the first 100 and does not paginate; a larger account gets
a subset in the picker and you can always type the id in full.

**Watchtower owns the `policies` array of a route that attaches rules.** If you previously attached a
reusable policy by hand to a `watchtower: {host}` application, that attachment stops surviving reconciles
once you attach rules to that route ([ADR-0040](../decisions/0040-the-edge-projection-is-authoritative.md)).
Express it as a *Cloudflare reusable policy* clause instead — or keep it as an **app-scoped** policy under
any name except the reserved `watchtower`, which no reconcile ever touches. Routes with no rules attached
keep today's behaviour, hand-made attachments included.

### New routes are protected by default

A route you create with a domain is **Authenticated** unless you say otherwise, so a service is never
published to the internet as the side effect of adding a route
([ADR-0035](../decisions/0035-new-routes-are-protected-by-default.md)). The default is a setting —
**Settings → Reverse proxy → Default access for new routes**, `authenticated` or `public` — and the
new-route form lets an admin choose the mode and the bypass paths per route.

Under this provider that only works if Cloudflare has somebody to let in, so **creating a protected
route is refused while no allow source is configured**: allowed emails, email domains, Access group ids
or reusable policy ids, any one of them. The message names the settings page and the Public alternative.
Configure an allow source before you create your first route, and the rest of this page applies as
written.

Watchtower's own routes ([ADR-0023](../decisions/0023-login-hosts-are-watchtower-self-routes.md)) and
LAN port routes ([ADR-0033](../decisions/0033-port-routes-and-internal-ca.md)) stay Public — a login
page that needs a session is a login page nobody can use, and a `host:port` address is not somewhere a
login redirect can return anyone to.

### Zone discovery

Watchtower assembles the list of domains it will offer you — under **Settings → Reverse proxy →
Primary domains** and in the new-route form — from up to three sources
([ADR-0036](../decisions/0036-routes-live-under-primary-domains.md)):

- the domains you configured yourself in **Primary domains** (any provider);
- the zones your API token can list, which needs `Zone:Read` (this provider only);
- the configured zone id, when there is one — its name is read off any DNS record in it, so a token
  without `Zone:Read` still gets its one zone.

A domain you configured wins over a zone of the same name. Discovery is **cached for about five
minutes**, keyed by your credentials, so changing the token takes effect without a restart. It also
**fails open**: if the listing errors, you get fewer domains rather than an error page, and you can
always type a hostname in full.

The same list decides where a DNS record goes: the zone whose name is the **longest suffix** of the
route's domain, so an account holding both `example.com` and `apps.example.com` sends
`web.apps.example.com` to the more specific one. A domain no zone covers — and no configured zone id to
fall back to — leaves its route at `Error` naming both remedies.

**Beyond 50 zones, set the zone id.** The listing asks for the first 50 and does not paginate yet, so a
larger account gets an arbitrary subset; a route under one of the others then relies on the configured
zone id.

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
- **The application audience is injected too.** Each reconcile records the Access application's
  **AUD tag** on the route, and the stack's next deploy injects it as `WATCHTOWER_AUTH_AUDIENCE`
  (comma-separated when the stack has several protected hostnames; the route's own hostname instead
  under integrated auth). Apps should check it —
  [ADR-0037](../decisions/0037-assertions-carry-an-injected-audience.md). Every application in your
  account is signed by the same team key and published under the same JWKS, so an app that verifies
  the signature and ignores `aud` will accept an assertion minted for *any* of them, including ones
  with a far wider allow-list than this route's. A newly created route has no AUD tag until its first
  reconcile, so the variable appears on the deploy after it.
- **TLS mode:** upstream connections from cloudflared to your services are plain HTTP on the private
  ingress network, like Caddy's; the route's `TlsEnabled` flag is not consulted by this provider.
