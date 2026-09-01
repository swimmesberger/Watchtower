# Built-in reverse proxy (in-process) — operator guide

The default provider since [ADR-0022](../decisions/0022-in-process-yarp-proxy.md). Watchtower binds
the ingress ports itself, terminates TLS with certificates it obtains from an ACME CA, and forwards
each request to the routed container over that stack's private ingress network. There is **no second
container, no control network and no admin API** — the proxy is Watchtower.

The feature is **opt-in**. While it is off, nothing here happens: the host dispatcher sees an empty
route table and every request falls through to Watchtower's own pipeline.

For what all three providers share, start at [README.md](README.md).

## Ports: ingress is not the management plane

Watchtower binds **three** listeners when the built-in proxy is on — plus one more for each port route
you create — and the split is load-bearing rather than tidy.

| Container port | Endpoint | What it is | Publish it as |
| --- | --- | --- | --- |
| 8080 | `Http` | **Management plane** — Watchtower's own UI, `/rpc` and `/api/*`. Answers for every host name. Set in the image (`Kestrel__Endpoints__Http__Url`). | `127.0.0.1:8080:8080` — a private interface, never the internet |
| 8081 (default) | `ProxyHttp` | **Ingress, plain HTTP** — ACME HTTP-01 validation and the plain half of the proxy. Setting: **Ingress HTTP port**. | `80:8081` |
| 8443 (default) | `ProxyHttps` | **Ingress, TLS** — the routed traffic, one certificate per SNI name. Setting: **Ingress HTTPS port**. | `443:8443` |
| one per port route | `ProxyPort{n}` | **Ingress, TLS, one service** — a LAN address with no domain, certified by Watchtower's own CA. Comes and goes with the route, no restart. See [port routes](README.md#port-routes-https-on-a-lan-with-any-provider). | `{n}:{n}` |

A port route's listener is **ingress** in the sense the split cares about: Watchtower's management plane
is never served on it. What it does *not* share is the host lookup — the `Host` header decides
nothing there, so the listener serves its own route and only its own route, whatever name the client
wrote in. The one time it answers 404 is the moment after a deletion, when the route is gone and the
socket is not yet unbound.

The two ingress listeners are **reverse-proxy settings, not image configuration**: they exist only
while the built-in provider is enabled, and their container ports are editable under **Settings →
Reverse proxy** (or pinned with `WATCHTOWER__PROXY__YARP__HTTPPORT` / `__HTTPSPORT`). Setting a port
to `0` turns that listener off. Whatever you publish on the host side has to match the container port
the listener is actually on.

On the ingress ports a request whose `Host` is **not** in the route table is answered with a bare
**404** — no body, no redirect, nothing that says what else is here. The one Watchtower hostname
ingress will serve is a realm's **login host** (`WATCHTOWER__AUTH__HOST`), which is how you reach the
UI from outside: over 443, on a name you chose, with authentication on. The rule runs the other way
too — a routed application's domain is refused on 8080, so ingress traffic is never half-served on the
port you kept private.

That is the invariant the Caddy provider had for free (a request with no matching site block got
nothing) and it is why 8080 is a separate listener now: published straight to the internet, a shared
endpoint serves `http://<your-ip>/` the login page with authentication on, and the whole UI with it
off.

## HTTPS on a LAN, without a domain: port routes

Port routes are **not** the built-in provider's — they are a listener on Watchtower's own container and
work alongside Caddy and the Cloudflare Tunnel too. The operator guide moved with them:
[README.md → Port routes](README.md#port-routes-https-on-a-lan-with-any-provider).

What is specific to this provider is the collision rule. A port route's listen port must differ from the
two ingress ports above as well as from the management port; the projection drops a listener that
collides with one and says so in the log, and `proxy.updateConfig` refuses an ingress port that an
existing port route already holds.

## Enabling it

Publish the two ingress ports on Watchtower's own container, then turn the proxy on.

```yaml
services:
  watchtower:
    ports:
      - "127.0.0.1:8080:8080"   # the UI and API — reach it over a VPN or an SSH tunnel
      - "80:8081"               # ACME HTTP-01 validation, and the plain-HTTP half of the proxy
      - "443:8443"              # the routed traffic
    environment:
      WATCHTOWER__PROXY__ENABLED: "true"
      WATCHTOWER__PROXY__ADMINEMAIL: you@example.com   # recommended, for expiry notices
      # WATCHTOWER__PROXY__PROVIDER: yarp              # the default; state it only to be explicit
      # WATCHTOWER__PROXY__YARP__HTTPPORT: "8081"      # the defaults; setting them here pins them
      # WATCHTOWER__PROXY__YARP__HTTPSPORT: "8443"
```

**Port 80 is not optional.** HTTP-01 is the only challenge type this implements, and the CA reaches
the challenge responder over plain HTTP on port 80 by definition. Without it no certificate is ever
issued.

The management port comes from `Kestrel__Endpoints__Http__Url` (8080), set in the image; to move it,
override that variable by name — **`ASPNETCORE_URLS` does not apply to this image** and Kestrel
ignores it with a warning.

Outside the image — a bare `dotnet run`, a systemd unit, the Aspire AppHost — there is usually no named
endpoint at all, and Kestrel binds `ASPNETCORE_URLS` instead. That only works while *nothing* is
configured, and enabling the proxy configures something. So when the proxy is on, Watchtower promotes
the first plain-`http://` hosting URL into the management endpoint for you; the management listener
stays exactly where it was. With the proxy off nothing is touched.

An ingress port set to the management port is refused rather than bound (it would be a duplicate bind,
and it would make the management port ingress — which is precisely what the split exists to prevent).
Watchtower logs a warning and leaves that one listener off.

The two ingress ports are settings: **Ingress HTTP port** and **Ingress HTTPS port** in the yarp block
of Settings → Reverse proxy, or `WATCHTOWER__PROXY__YARP__HTTPPORT` / `__HTTPSPORT`. `0` turns a
listener off — HTTPS off is what you want when something else terminates TLS in front of Watchtower,
HTTP off when nothing publishes 80 (no certificate will be issued then). With **both** off there is no
ingress at all, and the single remaining endpoint serves routes and the UI together, as it did before
the split — but only while there are no **port routes**. A port route's listener is ingress in its own
right, so creating one gives the deployment ingress again and undoes that collapse: a routed domain goes
back to being refused on 8080 with a bare 404, on an endpoint that was serving it a moment earlier.

### Switching at runtime

Nothing here needs a restart. Enabling the proxy binds the ingress listeners; disabling it, or
switching to `caddy` or `cloudflare`, unbinds them; changing a port rebinds that one. The management
endpoint is untouched throughout, so the Settings page you made the change on stays reachable, and a
request already being served on a listener that is going away runs to completion before the socket
closes. A Caddy or Cloudflare deployment carries no ingress listener at all.

`Kestrel__Endpoints__ProxyHttp__Url` and `Kestrel__Endpoints__ProxyHttps__Url` were the knob before
this; they are **ignored** now, so a stale value left in a compose file does nothing.

Everything except `Kestrel__Endpoints__Http__Url` is editable at runtime under **Settings → Reverse
proxy**, and applies immediately. Env vars win over the UI
([ADR-0014](../decisions/0014-env-wins-runtime-settings.md)): a setting supplied that way shows as
pinned and read-only until the variable is removed.

## How a request flows

```
             :80 → 8081 ──────────┐                 ┌──────── :443 → 8443 (SNI → per-host certificate)
                                  ▼                 ▼
                    ┌─────────────────────────────────────────┐
                    │  Kestrel ingress (ProxyHttp/ProxyHttps)  │
                    └────────────────────┬────────────────────┘
                                         ▼
                    /.well-known/acme-challenge/*  ──► answered from the challenge store
                                         │              (never forwarded, never redirected)
                                         ▼
                         Host in the route table?  ── no ──►  404, and nothing else
                                         │
                                        yes  ── a Watchtower route ──►  Watchtower's own pipeline
                                         │                                (the UI and API, behind auth)
                                         ▼
                    strip every identity header the client sent
                                         ▼
                    /.watchtower/*  ──►  served locally, with X-Forwarded-Host/-Proto/-For stamped
                                         │   (the login callback, per-app logout, UserInfo)
                                         ▼
                    plain HTTP and the route wants TLS?  ── yes ──►  301 to https://…
                                         │
                                         ▼
                    route protected?  ── yes ──►  AccessVerifier ──► allow / login / 401 / 403
                                         │                              │
                                         ▼                              ▼
                    YARP IHttpForwarder  ◄──────── identity headers on the outgoing request
                                         ▼
                    http://{project}-{service}:{port}   (the stack's ingress network)
```

The dispatch happens **before** Watchtower's own routing, which is the point: on a route host,
Watchtower's literal endpoints (`/rpc`, `/api/*`, the SPA fallback) would otherwise out-rank any
catch-all and swallow the tenant's paths.

The management endpoint (8080) runs the same middleware with the branches reversed: an unknown host is
Watchtower's own UI, and a *routed* host is the 404. The challenge responder behaves identically on
both of them, because the CA does not get to choose which listener it reaches.

**A port route's listener takes a much shorter path**, and the diagram above does not apply to it. There
is no host lookup — the port decides the route, and the `Host` header decides nothing — so there is no
404 branch, no `/.watchtower/*` handling, no HTTP→HTTPS upgrade (the listener is TLS and there is no
plain leg) and no access check (a port route is public by construction). The identity-header strip is
the one step it keeps: nothing a client sends under those names reaches an upstream, on any listener.
**ACME challenges are not answered on a port-route listener either** — such a listener serves exactly
one upstream on a LAN address no CA validates, so answering there would hold a path the application is
entitled to serve itself, for a challenge that could never have been aimed at that address.

### Watchtower routes

A route whose target is **Watchtower (this instance)** rather than a stack service
([ADR-0023](../decisions/0023-login-hosts-are-watchtower-self-routes.md)) is the branch marked above:
the request takes Watchtower's ordinary pipeline — SPA, `/rpc`, `/api/*` and all — instead of being
forwarded anywhere. Forwarding it would be forwarding to ourselves. It is the one kind of host served
on **both** listeners: through ingress because that is how the UI and the login page are reachable from
outside, and on the management endpoint because that is how an operator who bound 8080 privately still
reaches them. It gets the HTTP→HTTPS upgrade like any other TLS route — the session cookie is set here,
and a page reached over plain HTTP would set it without its `Secure` attribute — and it gets an ACME
certificate and a status on the Routes page for the same reason every other row does.

## Certificates

### What is required

- **Port 80 reachable from the internet** (or from your internal CA), for HTTP-01.
- **DNS pointing at this host before the route is added.** Watchtower resolves the name and probes
  its own challenge responder through the public hostname before opening an order, so a domain whose
  DNS is not ready lands on `Awaiting DNS` instead of spending an ACME failure. (Split-horizon DNS
  can defeat the probe — turn it off with `WATCHTOWER__PROXY__YARP__ACMESELFCHECKENABLED=false`.)
- **Patience on the first issuance**: 10–30 seconds is normal, longer under load.

Until a host has a certificate, **HTTPS for it does not work at all** — the TLS handshake fails
rather than presenting something untrusted. That is deliberate: a browser warning that resolves
itself a minute later teaches people to click through warnings. Plain HTTP keeps working throughout,
which is also how the challenge is answered.

### Storage and renewal

Certificates are **rows in the database**, not files
([ADR-0024](../decisions/0024-postgresql-only-and-state-in-the-database.md)): the leaf and its
intermediates as PEM, the private key, the validity, the issuer and the thumbprint, one row per host
in `proxy_certificates`. The ACME account is a row too, one per directory URL, and the live HTTP-01
challenge tokens are rows with an expiry. `Proxy:Yarp:CertPath` is gone, and so is the
`/data/proxy-certs` directory it named — an existing one is imported once on the first start after
the upgrade and can then be deleted ([upgrading.md](../upgrading.md)).

Why rows: the in-memory SNI map is a *cache* of that table, so **every** instance answers every routed
host with the same certificate, whichever instance obtained it. A file on one node's volume could not.

**Encryption at rest is optional and worth turning on.** Set
`WATCHTOWER__AUTH__KEYPROTECTIONSECRET` to a long random passphrase and the private keys —
certificates, the ACME account, the internal CA, the identity-assertion signing key — are stored
AES-256-GCM encrypted under a key derived from it. Keep it out of the database (that is the entire
point) and out of your backups of the database. It covers the certificate keys, the ACME account key,
**the internal CA's signing key**, the identity-assertion signing key and the ASP.NET data-protection
key ring. Left unset, all five are stored exactly as the files on the data volume were — unencrypted —
and the host says so once at startup. Setting it later works: certificates are re-encrypted as they
renew, the signing key, the ACME account and the internal CA on the next start, and the key ring for
keys generated from then on.

**Losing a configured secret is worse than it used to be, in exactly one place.** Sessions are
invalidated and every ACME certificate is reissued automatically — that much is the blast radius losing
the key directory always had, and it resolves itself. The **internal CA does not**: an unreadable CA key
is treated as fatal to issuance rather than silently replaced, because minting a fresh root would
abandon the one every LAN client was told to trust and the failure would surface on those clients
instead of here. Issuance stops, the port routes go to `Error`, and recovery is manual — restore the
secret the row was written with, or delete the `internal_cas` row to generate a new CA and then
**re-import the new root on every device that trusted the old one**, a step per laptop and per phone. If
you use port routes, back the secret up accordingly.

### Which instance orders

Every instance **serves** certificates from the table; exactly one **orders** them. That one is
whichever instance currently holds the `acme-issuer` role lease, a row in `elarion_role_leases`
renewed on a heartbeat. Without the lease, three instances would open three ACME orders for every
host and spend the deployment's Let's Encrypt rate limit three times over to obtain one certificate.

The holder logs `This instance holds the acme-issuer lease and is ordering certificates.`; the others
log the corresponding "released" line naming the holder. **Renew now** on a non-holder is refused with
a message naming the instance that can do it — there is no request forwarding between instances yet,
and the holder's own pass picks the host up within five minutes anyway.

### How the other instances find out

A route, realm or certificate write bumps an internal setting, `Watchtower:Proxy:RoutesVersion`, and
every instance watches it through the settings store's PostgreSQL `LISTEN/NOTIFY` channel and
re-projects its route table and SNI map. It is internal: it never appears in the Settings UI and is not
one of the proxy card's paths. Do not pin it through the environment — that would freeze the one write
that tells the other instances anything changed. The instance handling the request also re-projects directly, so
it is correct before it answers — the signal is what brings the rest along a moment later.

Renewal starts once a certificate is into the **last third of its lifetime** — a fraction rather than
a fixed 30 days, so a 90-day Let's Encrypt certificate and a 24-hour test certificate both get a
sensible window. Failures back off along a ladder (1m, 5m, 15m, 1h, 3h, 6h, 12h, 24h); a validation
failure starts at the third rung (15 min), because Let's Encrypt allows only five failed validations
per hostname per hour, and a terminal failure goes straight to the longest rung.

### Let's Encrypt rate limits

The one that bites a multi-tenant deployment is **50 certificates per registered domain per week**.
Each tenant subdomain is its own certificate, so an estate of 60 tenants under one domain cannot be
onboarded in a week from a standing start.

- **Onboard against the staging directory first.** Set
  `WATCHTOWER__PROXY__YARP__ACMEDIRECTORYURL=https://acme-staging-v02.api.letsencrypt.org/directory`
  (or type it into Settings → Reverse proxy). Staging certificates are untrusted — browsers will
  complain — but the limits are far higher, and it proves DNS, ports and routing before the real
  budget is touched. Switch back and let the certificates re-issue.
- There is no way around the ceiling itself with per-host certificates. The two structural answers
  are a **wildcard certificate via DNS-01** and an **on-premises CA such as step-ca**; DNS-01 is not
  implemented today, so step-ca is the available one.
- Failed *validations* are limited separately (five per hostname per hour), which is why the backoff
  is deliberately slow for those.

### Two kinds of "internal CA", and they are different things

Both are supported, they compose, and neither replaces the other.

- **Watchtower *is* a CA** — the one above, for **port routes** only. It generates its own root, signs
  one certificate covering the LAN names, and you import that root by hand. It exists because no ACME CA
  of any kind can issue for `nas.lan` or a bare IP. Its root, the LAN names it covers and the current
  certificate's expiry are shown in the **Internal CA** block on the Routes page.
- **Watchtower *talks to* a CA** — the section below, for **domain routes**. Point the ACME directory
  URL at an on-premises CA such as step-ca and every ordinary domain route gets its certificate from
  there over RFC 8555, exactly as it would from Let's Encrypt.

A deployment can do both at once: domains over ACME, LAN ports from the built-in CA. What Watchtower
signs itself is never offered to a domain route, and never enters the ACME desired set.

### An internal CA (step-ca, ZeroSSL, …)

Any RFC 8555 CA works:

```yaml
environment:
  WATCHTOWER__PROXY__YARP__ACMEDIRECTORYURL: https://ca.internal.example/acme/acme/directory
  WATCHTOWER__PROXY__YARP__ACMECABUNDLEPATH: /data/internal-ca.pem   # this CA's root, in PEM
  # Only if the CA requires External Account Binding:
  WATCHTOWER__PROXY__YARP__ACMEEABKEYID: kid-from-your-ca
  WATCHTOWER__PROXY__YARP__ACMEEABHMACKEY: base64url-hmac-key
```

The CA bundle is **additive**: those roots are trusted alongside the system store when talking to the
directory, never instead of it. The path must be absolute and readable inside the container — it is
validated when you save, so a typo fails there rather than as a background warning weeks later. The
EAB key id and HMAC key are a pair: set both or neither.

## Access control

Identical in effect to the Caddy provider — the same `AccessVerifier` produces the verdict, so the two
cannot disagree about who may enter an app. What differs is only the mechanism:

- **No forward-auth hop.** The decision is a method call, not an HTTP request to
  `/api/access/verify`. That endpoint still exists for Caddy deployments.
- **Identity headers are set on the outgoing request** rather than lifted off a forward-auth response
  by `copy_headers`. The full identity vocabulary of both ecosystems (`Remote-*`,
  `X-Auth-Request-*`, `X-Watchtower-*`) is stripped from every inbound request first, on every route,
  so nothing a client sends under those names can reach an application.
- **`X-Forwarded-Method` and `X-Forwarded-Uri` are removed and ignored.** In process the real method
  and URI are right here; honouring the headers would let a POST present itself as a navigation and
  collect a login redirect instead of the 401 it is owed.
- **`/.watchtower/*` is served by Watchtower on the app's own domain**, with `X-Forwarded-Host`
  stamped, because the authorization-code callback binds the code to the domain it is redeemed on.

See [docs/central-auth/README.md](../central-auth/README.md) for the feature itself, and §6.2 of the
[design](../central-auth/design.md) for the side-by-side with the generated Caddyfile.

## Behind another TLS terminator

If a load balancer or cloud ingress already speaks HTTPS to the visitor and forwards plaintext to
Watchtower, turn the upgrade off:

```yaml
WATCHTOWER__PROXY__YARP__REDIRECTHTTPTOHTTPS: "false"
```

Left on, Watchtower would redirect a request that already arrived over HTTPS at the edge, and the
visitor would loop. Consider turning the TLS listener off entirely in that setup — **Ingress HTTPS
port** to `0` — since nothing reaches it.

## Watching it work

The **Routes** page (`/routes`) shows a **Certificates** card under this provider: one row per host
the proxy wants a certificate for — routed domains, realm login hosts, any orphan whose route is gone
but whose certificate has not expired, and, once a port route exists, the internal CA's shared LAN leaf
(`internal-lan.watchtower.invalid`, source **Internal CA**) that every port route is served with — each
with its state, expiry, last error and next scheduled attempt. It refreshes every 30 seconds while the
page is open. The leaf's host name is a store key, not an address anybody types: the certificate is
chosen by listening port, and the names you reach it on are the LAN names in its SANs.

**Renew now** (`proxy.renewCertificate`) orders immediately, ignoring both the renewal window and any
backoff rung. It is the escape hatch for "I have just fixed the DNS and do not want to wait six
hours". It only accepts hosts the proxy already wants a certificate for, and only on the instance
holding the `acme-issuer` lease — elsewhere it answers with a conflict naming that instance. The LAN
leaf's row carries no button at all, and the RPC refuses it by name if you reach for it directly: that
certificate comes from Watchtower's internal CA rather than from a public one over ACME, and it reissues
itself when the LAN names change or it nears expiry.

## Troubleshooting

**The browser reports a TLS handshake failure.** There is no certificate for that host yet. Check the
route's status detail on the Routes page and the host's row in the Certificates card: `Awaiting DNS`
means the name does not resolve here, `Error` carries the CA's own words. Plain HTTP will still
answer.

**The status says "HTTPS ingress disabled (port 0)".** That is a setting, not a failure — someone set
**Ingress HTTPS port** to `0`. Give it 8443 (or whatever you publish 443 onto) and the listener comes
up immediately. Routes resolve and are served meanwhile, but over plain HTTP only.

**A port is already in use.** Two different failures wear that name, and which one you get depends on
how Watchtower is installed. Where its listeners share the host's network namespace — a bare process, a
systemd unit, a container on `network_mode: host` — the conflict is Kestrel's own bind, and that
behaves differently depending on *when* it happens.

- **At startup it is fatal.** Kestrel throws `IOException: Failed to bind to address http://+:8443:
  address already in use` out of its start-up path and the process exits. Under host networking that is
  a crash-loop: `docker logs watchtower` has the line.
- **On a runtime change it is not.** When you move an ingress port from the Settings page and the new
  port is taken, Kestrel logs the failure at **Critical**, *keeps the listeners it already had*, and the
  process carries on serving. Nothing else changes — the management endpoint is untouched.

The second case is the one that bites, because it looks fine. The Settings page will report the new
port, traffic will keep arriving on the **old** one, and the instance will **crash-loop at the next
restart or self-update**, when that same bind is attempted from the fatal path. Watchtower notices what
it can: the proxy status shows `ingress port 8443 failed to bind — see the logs` when the server is not
listening on a port the configuration asks for. **After changing an ingress port, check the status and
the container log before walking away.** Pick a free container port under Settings → Reverse proxy and
republish it on the host side to match.

**In the ordinary containerised deployment neither of those happens.** Kestrel binds 8443 *inside* the
container, and another container's published host port cannot reach into that namespace. What is
contended there is the **daemon's host-port allocation**, and it fails one step earlier: the container
asked to start second is created but never starts. So publishing a port route's host port reports
`Bind for 0.0.0.0:9001 failed: port is already allocated`, the recreate rolls back, and the route says
*host port not published* again; a stack service that wants a port Watchtower already holds fails its
own `compose up` with the same line. Neither message mentions the proxy.

Finding the holder: `docker ps --format '{{.Names}}\t{{.Ports}}' | grep 9001` from a terminal — running
containers only, so a stopped stack that publishes the port will not appear there, while the refusal
Watchtower makes when you create the route counts containers in any state. The exposure map on the
**Infrastructure** page includes those, with the stack and service beside them. The fix is to take that
`ports:` entry out of the stack's compose file — a routed service needs none, and the proxy plane's
ports are the one set a stack must leave alone
([what a routed stack must not do](README.md#what-a-routed-stack-must-not-do)).

The old listener is not a hole while it lasts. Watchtower decides what is ingress by exclusion — while
the proxy is on, every port except the management one is ingress — so the stranded listener keeps
answering unknown hosts with a 404 rather than turning into a second management endpoint. The same rule
means **any extra endpoint you add to `Kestrel:Endpoints:*` is treated as ingress** while the proxy is
on: it serves routed hostnames and Watchtower's own, and 404s everything else. To move the management
plane, change `Kestrel__Endpoints__Http__Url` rather than adding a second endpoint.

**Nothing reaches the proxy at all.** Confirm the proxy is enabled and the built-in provider selected
— the ingress listeners exist only then. Then confirm `80:8081` and `443:8443` are published on
Watchtower's container (`docker port watchtower`), that the right-hand sides match the configured
ingress ports, and that nothing else on the host holds 80 or 443. If you are upgrading from a mapping
of `80:8080`, that is the change: 8080 is the management plane now, and 8081 is plain-HTTP ingress.

**A domain you added answers 404 instead of your app.** The request reached the management endpoint
rather than ingress — `80:8080` instead of `80:8081`, or a browser going straight to `:8080`. Routes
are served on the ingress ports only.

**You cannot reach the UI any more after publishing 8080 on loopback.** That is the intended shape.
Tunnel to it (`ssh -L 8080:127.0.0.1:8080 you@host`), or enable authentication, set
`WATCHTOWER__AUTH__HOST` to a hostname pointed at this host, and use it over 443 — that login host is
the one name ingress serves Watchtower itself on.

**A port route's address refuses the connection entirely.** The host port is not published on
Watchtower's container. The Routes page says so per row (*host port not published*) and offers to
publish it; `docker port watchtower` is the check from a terminal. Inside the container the listener is
already up — that half is a route change, not a restart — so nothing else needs fixing.

**A port route says "not published" again after a `docker compose up -d`.** That is the expected
behaviour, not a regression: compose rebuilt the container from the compose file and dropped the port
Watchtower added, and the startup reconcile noticed. Press the button again, and this time add
`- "9001:9001"` to the compose file so the next `up` keeps it.

**The browser does not trust a port route's certificate.** Two causes, and the error message
distinguishes them.

- *"Not trusted" / "unknown authority"* — the root has not been imported on this device, or was imported
  without the trust flag (macOS and Firefox both need that second step). Download it again from the
  Routes page and re-check step 4 above.
- *"The name does not match" / `NET::ERR_CERT_COMMON_NAME_INVALID`* — the address you typed is not in
  **LAN names**. Add it under Settings → Reverse proxy; the certificate is reissued immediately and no
  device has to re-import anything, because the root did not change. A host name and its IP are two
  different entries: listing `nas.lan` does not make `https://192.168.1.10:9001` work.

**A port route is `Active` but its listener never came up.** The projection refuses to bind a port-route
listener whose port is the management port or one of the two ingress ports, and warns in the log rather
than touching the row. The route validation refuses those at create time, so this only happens when an
*ingress* port moved onto an existing route's port afterwards — which the Settings page also refuses,
unless the value came from the environment, where nothing checks it. Move one of the two.

**"A container recreate started by … is still in progress."** The self-update and the port publish both
recreate Watchtower's own container, and they refuse to run at the same time — two coordinators racing
over one container id is how you get a stopped container with no rollback. Wait for the other one to
finish. If it is genuinely wedged, the message names its container: remove that container and restart
Watchtower. This is deliberately not cleared on a timer.

**Validation keeps failing and you want to see why, locally.** Run [Pebble](https://github.com/letsencrypt/pebble),
a deliberately pedantic test CA:

```bash
docker run --rm -p 14000:14000 -p 15000:15000 ghcr.io/letsencrypt/pebble:latest \
  -config /test/config/pebble-config.json -strict
curl -sk https://localhost:15000/roots/0 > /tmp/pebble-root.pem
```

Then point Watchtower at it: `WATCHTOWER__PROXY__YARP__ACMEDIRECTORYURL=https://localhost:14000/dir`
and `WATCHTOWER__PROXY__YARP__ACMECABUNDLEPATH=/tmp/pebble-root.pem` (Pebble's own
`test/certs/pebble.minica.pem` works as the bundle too, if you have the source tree). Pebble has to be
able to reach your HTTP listener on the port its `httpPort` names, so this is a check for a human at a
terminal rather than something to wire into CI.
