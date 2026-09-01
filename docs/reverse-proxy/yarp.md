# Built-in reverse proxy (in-process) — operator guide

The default provider since [ADR-0022](../decisions/0022-in-process-yarp-proxy.md). Watchtower binds
the ingress ports itself, terminates TLS with certificates it obtains from an ACME CA, and forwards
each request to the routed container over that stack's private ingress network. There is **no second
container, no control network and no admin API** — the proxy is Watchtower.

The feature is **opt-in**. While it is off, nothing here happens: the host dispatcher sees an empty
route table and every request falls through to Watchtower's own pipeline.

For what all three providers share, start at [README.md](README.md).

## Ports: ingress is not the management plane

Watchtower binds **three** listeners when the built-in proxy is on, and the split is load-bearing
rather than tidy.

| Container port | Endpoint | What it is | Publish it as |
| --- | --- | --- | --- |
| 8080 | `Http` | **Management plane** — Watchtower's own UI, `/rpc` and `/api/*`. Answers for every host name. Set in the image (`Kestrel__Endpoints__Http__Url`). | `127.0.0.1:8080:8080` — a private interface, never the internet |
| 8081 (default) | `ProxyHttp` | **Ingress, plain HTTP** — ACME HTTP-01 validation and the plain half of the proxy. Setting: **Ingress HTTP port**. | `80:8081` |
| 8443 (default) | `ProxyHttps` | **Ingress, TLS** — the routed traffic, one certificate per SNI name. Setting: **Ingress HTTPS port**. | `443:8443` |

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

A route is normally a **domain**. On a box with no domain and no public DNS — a NAS on `nas.lan` at
`192.168.1.10` — no CA will ever issue for it, and there is nothing in a TLS handshake from a bare
address to tell one service from another: a browser dialling an IP sends no SNI name at all. So the
built-in proxy offers a second kind of route ([ADR-0033](../decisions/0033-port-routes-and-internal-ca.md)):

> A **port route** gives one stack service a **TLS port of its own** on this host — `https://nas.lan:9001`
> — with a certificate from a certificate authority Watchtower generates for itself and you import once.

The service still needs no `ports:` of its own; the proxy reaches it over the stack's ingress network,
exactly as it reaches a routed domain. A port route is always **public** (there is no hostname for a
login redirect to return anyone to), always **TLS**, and always points at a **stack service** — never at
Watchtower itself. It is served by the built-in provider only: under Caddy or Cloudflare such a route
shows `Error` and says so.

The whole workflow, once:

### 1. Set the LAN names

**Settings → Reverse proxy → LAN names.** List every address anyone will actually type, separated by
commas or newlines:

```
nas.lan, 192.168.1.10
```

Both forms matter and neither substitutes for the other — a browser asked for `https://nas.lan:9001`
matches a DNS entry, and one asked for `https://192.168.1.10:9001` matches only an IP entry. These
become the subject alternative names of the one certificate Watchtower issues for **all** port routes,
so adding a name later reissues it for every route at once. Pinnable as
`WATCHTOWER__PROXY__YARP__LANNAMES`.

Leave it empty and the internal CA is simply unused — nothing is generated until something needs it.
Creating a port route with no LAN names configured is refused, because the certificate has to carry the
name you will type.

### 2. Create the port route

**Routes → Add route → Port.** Pick the stack, the compose service, its container port, and the **listen
port** — the number on this host clients will address it by (`9001`). The port has to be free: the
management port, the two ingress ports and any other port route are all refused, with the reason.

The route goes `Active` as soon as the certificate is issued, which is immediate — the instance you are
talking to issues it itself rather than waiting for a background pass.

### 3. Publish the host port on the Watchtower container

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

**Settings → Reverse proxy**, or the **Internal CA** block on the Routes page: *Download CA certificate*
(`/api/proxy/internal-ca.crt`, PEM; add `?format=der` for the binary form some import dialogs insist
on). Then install it as a trusted root on every device that should reach these addresses:

| Client | Where |
| --- | --- |
| macOS | Keychain Access → System → drag the file in → open it → **Trust → Always Trust** |
| Windows | Double-click → Install Certificate → Local Machine → **Trusted Root Certification Authorities** |
| Linux | Copy to `/usr/local/share/ca-certificates/watchtower-internal-ca.crt`, then `sudo update-ca-certificates` |
| Firefox | Has its own store: Settings → Privacy & Security → Certificates → View Certificates → Authorities → **Import**, tick "identify websites" |
| Android | Settings → Security → Encryption & credentials → Install a certificate → **CA certificate** (use the `?format=der` download) |
| iOS | Install the profile, then Settings → General → About → **Certificate Trust Settings** and enable it |

The root is valid for ten years and is never rotated automatically, so this is genuinely once per
device. It carries no secret — the signing key stays in the database — but the download sits behind the
management-plane login like the volume download does.

### 5. Browse it

`https://nas.lan:9001` — a normal padlock, no warning, no exception to click through.

### 6. Removing one

Deleting a port route unbinds its listener straight away. The host port stays published until you apply
the change; the Routes page then offers **Release ports & restart Watchtower**, which recreates the
container without the ports Watchtower itself added.



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
the split.

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
                    /.well-known/acme-challenge/*  ──► answered from the challenge store, always
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
Watchtower's own UI, and a *routed* host is the 404. Only the challenge responder behaves identically
on every port, because the CA does not get to choose which listener it reaches.

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
certificates, the ACME account, the identity-assertion signing key — are stored AES-256-GCM encrypted
under a key derived from it. Keep it out of the database (that is the entire point) and out of your
backups of the database. It covers the certificate keys, the ACME account key, the identity-assertion
signing key and the ASP.NET data-protection key ring. Left unset, all four are stored exactly as the
files on the data volume were — unencrypted — and the host says so once at startup. Setting it later
works: certificates are re-encrypted as they renew, the signing key and the ACME account on the next
start, and the key ring for keys generated from then on. Losing a configured secret invalidates sessions
and forces every certificate to be reissued, which is the same blast radius losing the key directory
always had.

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
the proxy wants a certificate for — routed domains, realm login hosts, and any orphan whose route is
gone but whose certificate has not expired — with its state, expiry, last error and next scheduled
attempt. It refreshes every 30 seconds while the page is open.

**Renew now** (`proxy.renewCertificate`) orders immediately, ignoring both the renewal window and any
backoff rung. It is the escape hatch for "I have just fixed the DNS and do not want to wait six
hours". It only accepts hosts the proxy already wants a certificate for, and only on the instance
holding the `acme-issuer` lease — elsewhere it answers with a conflict naming that instance.

## Troubleshooting

**The browser reports a TLS handshake failure.** There is no certificate for that host yet. Check the
route's status detail on the Routes page and the host's row in the Certificates card: `Awaiting DNS`
means the name does not resolve here, `Error` carries the CA's own words. Plain HTTP will still
answer.

**The status says "HTTPS ingress disabled (port 0)".** That is a setting, not a failure — someone set
**Ingress HTTPS port** to `0`. Give it 8443 (or whatever you publish 443 onto) and the listener comes
up immediately. Routes resolve and are served meanwhile, but over plain HTTP only.

**A port is already in use.** This behaves differently depending on *when* it happens, and the
difference matters.

- **At startup it is fatal.** Kestrel throws `IOException: Failed to bind to address http://+:8443:
  address already in use` out of its start-up path and the process exits. In Docker that is a
  crash-loop: `docker logs watchtower` has the line.
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
