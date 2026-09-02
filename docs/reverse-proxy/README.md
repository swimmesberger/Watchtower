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

## What a routed stack must not do

A routed service needs **no `ports:` at all**. That is the point of the topology above: the proxy dials
it over the stack's ingress network, so the only thing a published host port adds is a second, ungated
way in — and, with central auth, a way in that skips the access check entirely
([central-auth](../central-auth/README.md)).

There is one thing a stack's compose file genuinely **must not** do, and it is narrower than "avoid
`ports:`": it must not publish a host port the proxy plane itself uses. Which ports those are depends on
the provider:

| Provider | Host ports the stack must leave alone |
| --- | --- |
| **`yarp`** | 80 and 443 (or whatever you publish the ingress container ports 8081/8443 onto), and the management port (8080 by default). |
| `caddy` | 80/tcp, 443/tcp **and 443/udp** — all three are published by the Caddy container Watchtower manages (443/udp is HTTP/3). |
| `cloudflare` | None. The tunnel is outbound only and binds nothing on the host. |
| *any, if you use port routes* | **Every port route's listen port**, on top of the row above — a port route is host port == container port on Watchtower's own container by design ([ADR-0033](../decisions/0033-port-routes-and-internal-ca.md)), whichever provider serves your domains. |

Break it and the symptom depends on **how Watchtower is installed**, because the two shapes fail in
completely different places:

**Containerised (the default).** Nothing fights over a *listener*: Watchtower binds container port 8443
inside its own network namespace, and a stack container binds its own — different namespaces, no
conflict. What is contended is the **daemon's host-port allocation**, and the loser is whichever
container the daemon is asked to start second:

- **The stack got the port first.** Then it is Watchtower's recreate that fails. Publishing a port
  route's host port means recreating this container with the binding added; the daemon refuses to
  allocate a port it has already given away, the new container never starts, the recreate rolls back to
  the old one, and the route reports *host port not published* again — with an apply error carrying
  Docker's own words.
- **Watchtower got it first.** Then it is the stack's deploy that fails: `docker compose up` reports
  `Bind for 0.0.0.0:9001 failed: port is already allocated` on that one service and leaves the rest of
  the stack alone. Docker has no idea a reverse proxy is involved, so the message names the port and
  nothing else.

Either way `docker ps --format '{{.Names}}\t{{.Ports}}'` finds the holder from a terminal — running
containers only, which is a difference worth knowing, since the refusals below count containers in *any*
state. The exposure map on the **Infrastructure** page is the same answer with the stack and service
beside it, stopped containers included, and it groups host-port conflicts of its own accord.

**Bare process, systemd, or `network_mode: host`.** Here Watchtower's listeners really are in the same
namespace as everything else, so the failure is Kestrel's own bind: fatal at startup (the process exits —
a host-networked container therefore crash-loops), and merely *stale* on a runtime change — the new
listener does not come up, the old one keeps serving, and the instance crash-loops at the next restart
instead.
That asymmetry is the one that bites; [A port is already in use](yarp.md#troubleshooting) has both
halves. Nothing Watchtower can ask Docker sees a plain host process holding the port, so that half of
this shape is one the refusals below cannot help with — but a *container* holding it still can be, and
still is: a bare-process Watchtower binds host ports directly, so it is contending for the same numbers
the daemon hands out, and the check runs there with nothing to exclude.

**What Watchtower does about it.** Creating or editing a port route on a host port another container
already publishes is refused, naming that container and its stack and service; so is pressing *Publish
ports & restart Watchtower* for such a port, instead of letting the recreate fail and roll back. Any
container counts, in any state — a stopped stack comes back — and a UDP binding on the same number does
not, since these listeners are TCP. The check is a convenience, not a boundary, and it fails open in both
directions: where the Docker socket cannot be reached, and where Watchtower cannot identify its *own*
container, it refuses nothing and logs one warning. The second case is the important one — a port
Watchtower itself publishes must never be reported as held by something else, since publishing it by
hand and then adding the route is the documented manual path.

**What it does not do.** The deploy path does not inspect a routed service's `ports:`. A stack that
publishes 443 is deployed, and the collision surfaces wherever the failure lands — the daemon refusing
to allocate the port to whichever container starts second. Making the
deploy hold an opinion about that is [ADR-0029](../decisions/0029-blue-green-stack-deploys.md)'s
territory, which is still Proposed.

## Port routes: HTTPS on a LAN, with any provider

A route is normally a **domain**. On a box with no domain and no public DNS — a NAS on `nas.lan` at
`192.168.1.10` — no CA will ever issue for it, and there is nothing in a TLS handshake from a bare
address to tell one service from another: a browser dialling an IP sends no SNI name at all. So there is
a second kind of route ([ADR-0033](../decisions/0033-port-routes-and-internal-ca.md)):

> A **port route** gives one stack service a **TLS port of its own** on this host — `https://nas.lan:9001`
> — with a certificate from a certificate authority Watchtower generates for itself and you import once.

The service still needs no `ports:` of its own; Watchtower reaches it over the stack's ingress network,
exactly as it reaches a routed domain. A port route is always **public** (there is no hostname for a
login redirect to return anyone to), always **TLS**, and always points at a **stack service** — never at
Watchtower itself.

**It works with every provider.** The listener is on Watchtower's own container and the certificate comes
from Watchtower's own CA, so which backend terminates your public domains does not enter into it: a
Cloudflare Tunnel deployment can serve `app.example.com` through the tunnel and `https://nas.lan:9001`
on the LAN at the same time, and so can a Caddy one. The only switch that gates a port route is
**Settings → Reverse proxy → Enabled**. (Before the
[ADR-0033 addendum](../decisions/0033-port-routes-and-internal-ca.md#addendum-2026-09-01-port-routes-are-provider-independent)
they were built-in-provider-only and marked `Error` elsewhere.)

One consequence is worth stating, because it is new for Caddy and Cloudflare deployments: Watchtower's
own container joins the private ingress network of every stack it port-routes, so it can reach the
upstream. That is the same exposure the built-in provider has always had, now reaching exactly the stacks
you port-route and no others.

The whole workflow, once:

### 1. Set the LAN names

**Settings → Reverse proxy → LAN port routes → LAN names.** List every address anyone will actually
type, separated by commas or newlines:

```
nas.lan, 192.168.1.10
```

Both forms matter and neither substitutes for the other — a browser asked for `https://nas.lan:9001`
matches a DNS entry, and one asked for `https://192.168.1.10:9001` matches only an IP entry. These
become the subject alternative names of the one certificate Watchtower issues for **all** port routes,
so adding a name later reissues it for every route at once. Pinnable as
`WATCHTOWER__PROXY__PORTROUTES__LANNAMES`.

You should not have to work these out by hand, so the field offers **suggestions** as chips under it,
and clicking one appends it to the box. The first is the address you reached this page with, which the
browser knows for certain. The rest come from the server: the Docker host's own name, its completion in
whichever search domain this container resolves in, and the forward or reverse DNS counterpart of the
address you arrived on — a name for the address you typed, or the address for the name. A chip carries a
**check** when the address is confirmed: the one you arrived on always is, and for the others it means
forward and reverse resolution agree. An unchecked chip resolved only one way, or not at all from inside
the container, which says less than it sounds like — your laptop may resolve a name this container
cannot. Nothing is added until you click, and nothing is saved until you save. Treat
a suggestion as what it is: a name this deployment appears to answer on. Which names it *should* answer
on is still yours to decide.

Leave it empty and the internal CA is simply unused — nothing is generated until something needs it.
Creating a port route with no LAN names configured is refused, because the certificate has to carry the
name you will type.

### 2. Create the port route

**Routes → New route**, and pick **Port (LAN only, internal CA)** as the binding. Then the stack, the
compose service, its container port, and the **listen port** — the number on this host clients will
address it by (`9001`). The port has to be free: the management port, the built-in provider's two ingress
ports (where that provider is the one selected) and any other port route are all refused, with the
reason.

The route goes `Active` as soon as the certificate is issued, which is immediate — the instance you are
talking to issues it itself rather than waiting for a background pass.

### 3. Publish the host port on the Watchtower container

Pick a port no stack of yours publishes — the listener is on Watchtower's own container, so a stack that
binds the same host port takes it away ([what a routed stack must not do](README.md#what-a-routed-stack-must-not-do)).
Watchtower refuses a listen port another container already publishes, naming it.

The proxy is now listening on 9001 *inside* its container. Docker cannot add a published port to a
running container, so the Routes page offers to do the only thing that can: **Publish ports & restart
Watchtower (~5 s)**. Confirm it and Watchtower recreates its own container with `9001:9001` added,
keeping every other binding, volume and network it already had. The page — and any deploy or backup
running at that moment — is interrupted for a few seconds.

The manual equivalent, which is what you want on a bare-process install, on a multi-instance deployment
(each node has its own container), or in a compose file:

```yaml
services:
  watchtower:
    ports:
      - "127.0.0.1:8080:8080"
      - "9001:9001"           # one line per port route
```

**If Watchtower is compose-managed, add the line to the compose file even after using the button.** A
later `docker compose up -d` rebuilds the container from that file and drops anything the recreate
added; the routes then report "host port not published" again and the button comes back.

A port you published yourself is never adopted and never taken away — Watchtower only removes bindings
it added itself, and only when the route that asked for them is gone.

### 4. Download and import the root certificate

The **Internal CA** card on the Routes page has a **Download root** button; **Settings → Reverse proxy**
has the same thing as a *Download the internal CA root* link under the LAN names. Both fetch
`/api/proxy/internal-ca.crt` (PEM; add `?format=der` for the binary form some import dialogs insist on),
and neither appears until the CA exists — which is the first port route, not the first LAN name. Then
install it as a trusted root on every device that should reach these addresses:

| Client | Where |
| --- | --- |
| macOS | Keychain Access → System → drag the file in → open it → **Trust → Always Trust** |
| Windows | Double-click → Install Certificate → Local Machine → **Trusted Root Certification Authorities** |
| Linux (Debian, Ubuntu) | Copy to `/usr/local/share/ca-certificates/watchtower-internal-ca.crt`, then `sudo update-ca-certificates` |
| Linux (RHEL, Fedora, CentOS) | Copy to `/etc/pki/ca-trust/source/anchors/`, then `sudo update-ca-trust` |
| Linux (Arch) | Copy to `/etc/ca-certificates/trust-source/anchors/`, then `sudo trust extract-compat` |
| Firefox | Has its own store, on every platform: Settings → Privacy & Security → Certificates → View Certificates → Authorities → **Import**, tick "identify websites" |
| Android | Settings → Security & privacy → More security settings → Encryption & credentials → Install a certificate → **CA certificate** (the path is shorter on Android 11 and older; use the `?format=der` download). **Browsers will trust it; apps will not** — see below |
| iOS | Install the profile, then Settings → General → About → **Certificate Trust Settings** and enable it |

**Android caveat.** Since Android 7 a user-installed CA is trusted by Chrome and the other browsers but
**not by apps**, unless an app ships a `network_security_config` that opts into the user store. So a
port route works in a phone's browser and a native client of the same service may still refuse it, with
a certificate error that looks identical. There is no fix from Watchtower's side; the answers are to use
the browser, to use an app that opts in, or to put that service on a real domain with an ACME
certificate instead.

**Getting the file onto a phone or tablet.** The download sits behind the management-plane login, like
the volume download — so either sign in to Watchtower from that device and download it there, or fetch
it once on a desktop and transfer it out of band (AirDrop, a file share, a USB cable). There is no
anonymous URL to point a device at.

The root is valid for ten years and is never rotated automatically, so this is genuinely once per
device. It carries no secret: the signing key stays in the database and is never part of the download.

**If the browser still does not trust it**, the error message tells you which of the two causes it is.

- *"Not trusted" / "unknown authority"* — the root has not been imported on this device, or was imported
  without the trust flag (macOS and Firefox both need that second step). Download it again and re-check
  the table above.
- *"The name does not match" / `NET::ERR_CERT_COMMON_NAME_INVALID`* — the address you typed is not among
  the **LAN names**. A host name and its IP are two separate entries: listing `nas.lan` does not make
  `https://192.168.1.10:9001` work. Add it under **Settings → Reverse proxy → LAN port routes**; the
  certificate is reissued as soon as the setting is saved, and no device has to re-import anything,
  because the root did not change.

### 5. Browse it

`https://nas.lan:9001` — a normal padlock, no warning, no exception to click through.

### 6. Removing one

Deleting a port route unbinds its listener straight away. The host port stays published until you apply
the change; the Routes page then offers **Release ports & restart Watchtower**, which recreates the
container without the ports Watchtower itself added.

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
