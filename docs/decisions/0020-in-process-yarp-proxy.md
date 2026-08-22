# ADR-0020: The reverse proxy runs in Watchtower's own process (YARP + ACME), and is the default

- Status: Accepted
- Extended by: [ADR-0021](0021-login-hosts-are-watchtower-self-routes.md) — the login hosts this ADR
  describes as synthesised `Local` sites are now ordinary `Watchtower`-target route rows.
- Date: 2026-08-22
- Related: [ADR-0015](0015-proxy-provider-abstraction.md) (the provider seam this extends),
  [ADR-0014](0014-env-wins-runtime-settings.md) (the runtime settings and env pins it rides on),
  [ADR-0007](0007-pluggable-metrics-backend.md) (the runtime-router pattern both copy),
  [docs/central-auth/design.md](../central-auth/design.md) (the access contract it re-implements
  in process),
  [docs/reverse-proxy/yarp.md](../reverse-proxy/yarp.md) (the operator guide).

## Context

ADR-0015 turned the proxy plane into a seam with two backends: a sibling **Caddy** container, and a
**Cloudflare Tunnel**. Caddy was the default and carried real cost for what is, in the end, a
host-header lookup and a forward:

- an image to pull and a second container to supervise, on a host that already runs Watchtower;
- a `watchtower-control` network whose only purpose is letting Caddy call back into Watchtower;
- an admin-API hop on every route change (build a config document, POST it to `/load`);
- a second TLS stack, with its own certificate storage that Watchtower can only guess at — which is
  why `Route.Status` was documented as "indicative" and never became real;
- an **HTTP forward-auth hop per protected request**: Caddy calls `/api/access/verify` over the
  control network, waits, then forwards. The decision is Watchtower's either way; the hop is pure
  overhead, and it forced a second rendering of every input (`X-Forwarded-Method`, `X-Forwarded-Uri`)
  that a client can also simply write.

The workloads Watchtower targets are on-prem and cloud-free: a NAS, a small server, an appliance.
For those, "one process, one port mapping" is the shape that matters, and Cloudflare — which solves
the no-open-ports case well — is not an answer, because it moves ingress and TLS into someone else's
account.

.NET already ships the two pieces this needs: **YARP** for forwarding, and Kestrel's per-connection
TLS callbacks for SNI. What it does not ship is an ACME client, so certificates were the real
question rather than the routing.

## Decision

1. **A third `IProxyProvider`, `yarp`, that runs in Watchtower's own process — and it is the new
   default.** No sibling container, no control network, no admin API. `Proxy:Provider` resolves
   unknown or blank values to `yarp`; `caddy` and `cloudflare` remain selectable, and Caddy is
   **deprecated**.

2. **Forwarding is YARP's direct forwarder (`IHttpForwarder`) from a host-dispatch middleware placed
   before Watchtower's own routing** — not YARP's route/cluster configuration model. The reason is
   endpoint routing: on a route host, Watchtower's own literal endpoints (`/rpc`, `/api/*`, the SPA
   fallback) out-rank a catch-all, so a config-driven catch-all route would lose to them for exactly
   the paths a tenant application is most likely to use. Dispatching by `Host` ahead of routing makes
   the decision "whose request is this?" instead of "which endpoint wins?", which is the question
   actually being asked.

3. **TLS is a named Kestrel endpoint, `ProxyHttps`, with `TlsHandshakeCallbackOptions` serving
   prebuilt `SslStreamCertificateContext`s** (leaf plus intermediates, built once, offline). Not the
   plain per-SNI certificate selector: that hands chain building to `SslStream` at handshake time,
   which resolves intermediates per connection and can reach out to build a chain. The endpoint is a
   named one because the callback attaches per listener; it exists only where it is configured, so
   development, Aspire and the integration tests boot the host unchanged.

4. **A hand-written RFC 8555 client, HTTP-01 only.** TLS-ALPN-01 is not implementable on Kestrel (it
   needs the ALPN callback to swap in a throwaway certificate for the `acme-tls/1` protocol, which
   Kestrel does not expose), and DNS-01 would mean provider credentials — a feature, not a
   dependency. One order per host, issued eagerly rather than on demand, with a DNS preflight and a
   self-check through the public hostname before the order opens, so a domain whose DNS is not ready
   fails as `AwaitingDns` instead of spending an ACME failure. Certificates and the account key are
   PEM files under `/data/proxy-certs`, inside the existing data volume.

5. **Any RFC 8555 CA, not only Let's Encrypt.** `AcmeDirectoryUrl` selects the directory,
   `AcmeCaBundlePath` adds roots to the system trust store (additively — an internal CA's root, not a
   replacement), and the EAB pair covers CAs that bind accounts to a customer record. An on-premises
   step-ca is a first-class target of this ADR, not an afterthought.

6. **`AccessVerifier` is the single decision core, shared by both paths.** The Caddy
   `/api/access/verify` endpoint and the in-process middleware call the same class; there is no
   second implementation of "may this visitor enter?" to drift. In process the identity headers are
   set on the outgoing request rather than lifted off a forward-auth response by `copy_headers`, and
   the real method and URI are used instead of the `X-Forwarded-Method` / `X-Forwarded-Uri` strings a
   client can write.

7. **`/.watchtower/*` is served by Watchtower on the app's own host**, with `X-Forwarded-*` stamped —
   the same `handle /.watchtower/*` block Caddy generates, for the same reason: the callback binds an
   authorization code to the domain it is redeemed on, and it has to answer while the visitor is
   still anonymous. Realm login hosts are dispatched to the local pipeline rather than forwarded, and
   still get the HTTPS upgrade.

8. **Ingress and management are separate Kestrel endpoints.** Three named listeners: `Http` (8080) is
   the management plane — Watchtower's own UI and API, to be bound to a private interface and never
   published to the internet — while `ProxyHttp` (8081) and `ProxyHttps` (8443) are ingress, published
   as `80:8081` and `443:8443`. The dispatcher decides by host **and** by local port: on an ingress
   port a host that is not in the route table gets a bare **404**, and the only Watchtower hostname
   ingress will serve is a realm's **login host**. The mirror rule holds on the management port — a
   routed application's host is refused there too, so ingress traffic cannot be half-served on the
   endpoint an operator kept private. Setting either proxy endpoint's URL to an empty value turns that
   listener off, and with no ingress bound at all the single remaining endpoint serves everything, as
   before.

9. **`/api/proxy/ask` answers only under the `caddy` provider.** It is a route-existence oracle that
   exists for Caddy's on-demand-TLS module; the in-process proxy holds the route table in memory and
   Cloudflare's edge never asks, so under either it is not mapped at all.

## Consequences

- **Watchtower's own container now joins every tenant ingress network.** Previously that exposure
  belonged to the Caddy container; now the process holding the Docker socket, the database and every
  credential is reachable from each routed application's network. What is reachable there is
  Watchtower's whole HTTP surface — the SPA, `/rpc`, `/api/*` — so the unauthenticated surface is the
  thing to keep honest: `/health`, the token-authenticated `/api/webhooks/*`, `/api/app/*` and
  `/api/mgmt/*`, and the anonymous access endpoints (`/api/access/verify`, the `/.watchtower/*`
  callback, JWKS). `/api/proxy/ask` — the one endpoint that answered "does this domain exist?" for
  anyone who could reach it — is now unmapped under this provider, which closes the oracle rather
  than gating it. A tenant container was always able to reach Watchtower over its stack's ingress
  network under Caddy too; what changes is that it is now the same process, so the blast radius of a
  bug in that surface is larger.
- **The management plane is no longer one mistake away from the internet.** Sharing a single Kestrel
  endpoint between ingress and management made `80:8080` the obvious mapping and a fall-through the
  obvious behaviour for an unrouted host — which meant `http://<public-ip>/` served the login page with
  authentication on, and the entire UI, `/rpc` included, with it off. Nobody had to misconfigure
  anything: publishing the port the proxy needs was enough. Caddy never had this shape, because a
  request with no matching site block got nothing, and Watchtower was reachable through ingress on the
  login host alone. Splitting the listeners and refusing unknown hosts on ingress restores that
  invariant, and binding 8080 to a private interface is now the documented default rather than an
  operator's idea. The cost is a breaking change to the port mapping: `80:8080` becomes `80:8081`, and
  an upgrade that keeps the old mapping publishes the management plane exactly as before.
- **`Route.Status`, `Route.StatusDetail` and `Route.CertNotAfter` become real.** The proxy issues the
  certificates, so it knows their state, the last failure and the next attempt — which is what the
  Routes page's Certificates card and `proxy.renewCertificate` surface. Under Caddy these stay
  indicative.
- **`CookieSecure=Auto` finally sees native HTTPS.** The session cookie's `Secure` attribute is
  decided from the real connection instead of a forwarded header.
- **Caddy is deprecated.** It stays selectable and supported for existing installations; removing it
  is a separate decision and a separate ADR. `CaddyManager`, `CaddyConfigBuilder` and the
  `watchtower-control` network stay exactly as they are.
- **Existing installations are not switched silently.** Before this ADR the default was `caddy`, so
  an operator who added routes never had to name a provider — and the flip would have read that
  silence as "the in-process proxy", abandoning a running Caddy container, its certificates and its
  host ports on nothing but an image update. `ProxyProviderMigration` runs once at startup: with at
  least one route in the table and no provider stated in the environment or the settings store, it
  writes `caddy` into the settings store, logs it and records a `proxy`/`config.migrate` audit row.
  Whether the proxy is currently *enabled* is deliberately not part of the test — a pre-flip instance
  with routes was a Caddy installation whichever position that toggle is in today. Operators upgrade
  to the built-in provider when they choose to, from Settings → Reverse proxy.
- **The migration is guarded by a sentinel, not by the provider row.** It writes
  `Watchtower:Proxy:ProviderMigrated` on every start where it runs, *including* the starts that
  decline to pin. Without it the rule would be a trap rather than a migration: nothing writes
  `Proxy:Provider` in normal use, so a fresh install that took the new default, enabled the proxy and
  created its first routes would satisfy every remaining condition on its next restart and be dragged
  onto Caddy. Routes are evidence of the old default only on the first start after the upgrade, and
  the sentinel is what confines the question to that start. It is internal plumbing — never offered in
  the UI, absent from `GetProxyConfig.ProxyPaths`, not env-pinnable.
- **Let's Encrypt rate limits are a real constraint at tenant scale.** 50 certificates per registered
  domain per week, and eager issuance means a first start with many routes asks for them at once
  (bounded by `AcmeMaxConcurrentOrders`, deliberately small). Onboarding a large estate should run
  against the staging directory first. The structural answers — a wildcard via DNS-01, or an
  on-premises step-ca — are the two exits, and only the second is available today.
- **A host without a certificate has no HTTPS at all**: the handshake fails rather than presenting
  something. A self-signed placeholder was considered and rejected — a browser warning that resolves
  itself minutes later teaches operators to click through warnings. Operator-uploaded certificates
  are out of scope for the same round; both are follow-ups.
- **Development and the test suite are unaffected.** The TLS endpoint exists only where
  `Kestrel__Endpoints__ProxyHttps__Url` is configured, which is the shipped image and nothing else;
  the host-dispatch middleware costs one failed dictionary lookup per request while the provider is
  inactive.
- **`ASPNETCORE_URLS` no longer applies to this image.** Configured Kestrel endpoints override it, and
  Kestrel ignores it with a warning. Deployments that moved the port that way must set
  `Kestrel__Endpoints__Http__Url` instead.
