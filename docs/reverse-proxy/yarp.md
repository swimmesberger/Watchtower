# Built-in reverse proxy (in-process) — operator guide

The default provider since [ADR-0017](../decisions/0017-in-process-yarp-proxy.md). Watchtower binds
the ingress ports itself, terminates TLS with certificates it obtains from an ACME CA, and forwards
each request to the routed container over that stack's private ingress network. There is **no second
container, no control network and no admin API** — the proxy is Watchtower.

The feature is **opt-in**. While it is off, nothing here happens: the host dispatcher sees an empty
route table and every request falls through to Watchtower's own pipeline.

For what all three providers share, start at [README.md](README.md).

## Enabling it

Publish the two ingress ports on Watchtower's own container, then turn the proxy on.

```yaml
services:
  watchtower:
    ports:
      - "8080:8080"   # the UI and API, as before
      - "80:8080"     # ACME HTTP-01 validation, and the plain-HTTP half of the proxy
      - "443:8443"    # the routed traffic
    environment:
      WATCHTOWER__PROXY__ENABLED: "true"
      WATCHTOWER__PROXY__ADMINEMAIL: you@example.com   # recommended, for expiry notices
      # WATCHTOWER__PROXY__PROVIDER: yarp              # the default; state it only to be explicit
```

**Port 80 is not optional.** HTTP-01 is the only challenge type this implements, and the CA reaches
the challenge responder over plain HTTP on port 80 by definition. Without it no certificate is ever
issued.

The container-side ports come from `Kestrel__Endpoints__Http__Url` (8080) and
`Kestrel__Endpoints__ProxyHttps__Url` (8443), both set in the image. To move one, override that
variable by name — **`ASPNETCORE_URLS` does not apply to this image** and Kestrel ignores it with a
warning. Setting `Kestrel__Endpoints__ProxyHttps__Url` to an empty value turns the TLS listener off
entirely, which is what you want when something else terminates TLS in front of Watchtower.

Everything except the two `Kestrel__*` variables and `Proxy:Yarp:CertPath` is editable at runtime
under **Settings → Reverse proxy**, and applies immediately. Env vars win over the UI
([ADR-0014](../decisions/0014-env-wins-runtime-settings.md)): a setting supplied that way shows as
pinned and read-only until the variable is removed.

## How a request flows

```
                    :80 ─────────┐                 ┌──────── :443 (SNI → per-host certificate)
                                 ▼                 ▼
                    ┌────────────────────────────────────────┐
                    │  Kestrel (Http + ProxyHttps endpoints)  │
                    └────────────────────┬───────────────────┘
                                         ▼
                    /.well-known/acme-challenge/*  ──► answered from the challenge store, always
                                         │              (never forwarded, never redirected)
                                         ▼
                         Host in the route table?  ── no ──►  Watchtower's own pipeline
                                         │                     (UI, /rpc, /api/*, SPA)
                                        yes
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

PEM files and the ACME account key live under **`/data/proxy-certs`**, inside the existing data
volume — nothing extra to mount, and they survive a container recreate. The path is read at startup
(`WATCHTOWER__PROXY__YARP__CERTPATH`) and is shown read-only in the UI.

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
visitor would loop. Consider turning the TLS listener off entirely in that setup
(`Kestrel__Endpoints__ProxyHttps__Url=`), since nothing reaches it.

## Watching it work

The **Routes** page (`/routes`) shows a **Certificates** card under this provider: one row per host
the proxy wants a certificate for — routed domains, realm login hosts, and any orphan whose route is
gone but whose certificate has not expired — with its state, expiry, last error and next scheduled
attempt. It refreshes every 30 seconds while the page is open.

**Renew now** (`proxy.renewCertificate`) orders immediately, ignoring both the renewal window and any
backoff rung. It is the escape hatch for "I have just fixed the DNS and do not want to wait six
hours". It only accepts hosts the proxy already wants a certificate for.

## Troubleshooting

**The browser reports a TLS handshake failure.** There is no certificate for that host yet. Check the
route's status detail on the Routes page and the host's row in the Certificates card: `Awaiting DNS`
means the name does not resolve here, `Error` carries the CA's own words. Plain HTTP will still
answer.

**The status badge says "HTTPS listener not bound".** Kestrel never brought the TLS endpoint up.
Check `Kestrel__Endpoints__ProxyHttps__Url` — an empty value disables it deliberately — and the
container log for a bind failure. Routes still resolve and are served, but over plain HTTP only, which
is exactly the state this caveat exists to make visible.

**Nothing reaches the proxy at all.** Confirm `80:8080` and `443:8443` are published on Watchtower's
container (`docker port watchtower`) and that nothing else on the host holds 80 or 443.

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
