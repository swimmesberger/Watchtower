# ADR-0022: The reverse proxy runs in Watchtower's own process (YARP + ACME), and is the default

- Status: Accepted
- Extended by: [ADR-0023](0023-login-hosts-are-watchtower-self-routes.md) — the login hosts this ADR
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
   development, Aspire and the integration tests boot the host unchanged. *(What "configured" means
   changed in the addendum below: the endpoint is derived from the reverse-proxy settings rather than
   read from the environment.)*

4. **A hand-written RFC 8555 client, HTTP-01 only.** TLS-ALPN-01 is not implementable on Kestrel (it
   needs the ALPN callback to swap in a throwaway certificate for the `acme-tls/1` protocol, which
   Kestrel does not expose), and DNS-01 would mean provider credentials — a feature, not a
   dependency. One order per host, issued eagerly rather than on demand, with a DNS preflight and a
   self-check through the public hostname before the order opens, so a domain whose DNS is not ready
   fails as `AwaitingDns` instead of spending an ACME failure. Certificates and the account key are
   PEM files under `/data/proxy-certs`, inside the existing data volume. *(Superseded by
   [ADR-0024](0024-postgresql-only-and-state-in-the-database.md): certificates and the ACME account
   are rows, so any instance can serve any host.)*

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
   endpoint an operator kept private. Turning an ingress port off (`0`) removes that listener, and with
   no ingress at all the single remaining endpoint serves everything, as before. *The two ingress
   endpoints are reverse-proxy settings rather than image configuration — see the addendum.*

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
- **Development and the test suite are unaffected.** The TLS endpoint exists only where the
  reverse-proxy settings ask for it, which no development, Aspire or integration-test host does; the
  host-dispatch middleware costs one failed dictionary lookup per request while the provider is
  inactive.
- **`ASPNETCORE_URLS` no longer applies to this image.** Configured Kestrel endpoints override it, and
  Kestrel ignores it with a warning. Deployments that moved the port that way must set
  `Kestrel__Endpoints__Http__Url` instead.

## Addendum (2026-08-23): runtime-bound ingress endpoints

### Context

Decision 8 gave the proxy its own listeners but left them as *image* configuration: the Dockerfile set
`Kestrel__Endpoints__ProxyHttp__Url` and `Kestrel__Endpoints__ProxyHttps__Url`, Kestrel bound both at
startup whether or not the in-process provider was active, and the only way to turn one off was to
override the baked-in variable with an empty value — a hack, because Docker cannot unset a variable and
Kestrel's loader rejects an endpoint whose `Url` is present but blank, so the app had to filter the
blank endpoint out before the loader saw it. Three things followed. A Caddy or Cloudflare deployment
carried an idle TLS listener it had no use for. Enabling the proxy from Settings — a runtime switch in
every other respect — produced "HTTPS listener not bound, restart needed". And the ports were the one
part of the reverse-proxy configuration an operator could not see or change in the Settings UI.

Kestrel supports endpoint reload: `KestrelServerOptions.Configure(section, reloadOnChange: true)`
watches the section's change token and binds, unbinds and rebinds the `Kestrel:Endpoints:*` it finds.
The Elarion settings store is already a reloadable configuration source layered under the environment
(ADR-0014). The two compose.

### Decision

**The ingress listeners follow the reverse-proxy settings at runtime.** They exist if and only if
`Proxy:Enabled` **and** `Provider == yarp` **and** the port in question is non-zero — and the ports are
ordinary yarp settings, `Watchtower:Proxy:Yarp:HttpPort` (default 8081) and `HttpsPort` (default 8443),
editable in the yarp block of Settings → Reverse proxy and pinnable like any other.

- **One projected configuration decides it.** `ProxyIngressKestrelConfiguration.Build(root)` returns the
  configuration Kestrel is handed: everything under the host's `Kestrel` section *except*
  `Endpoints:ProxyHttp*`, plus the two proxy endpoints **derived** from the settings above. Kestrel gets
  it with `reloadOnChange: true`; the projection subscribes to the root configuration's reload token and
  raises its own only when the projected key set actually changed, so an unrelated settings write cannot
  rebind a public listener. `ProxyListenerStateInitializer` subscribes to the same token, which is what
  keeps the dispatcher's ingress rule and Kestrel's listeners describing one moment.
- **Stray `Kestrel__Endpoints__ProxyHttp*` values are masked, not honoured.** They were the old knob and
  may still be in an operator's compose file; a stale one would fight the derivation and a blank one
  would fail the host on startup. Masking them retires the "blank means off" filter along with it.
- **An ingress port equal to the management port is refused, not bound.** `HTTPPORT=8080` against the
  shipped image is a reachable mistake, and it would be two endpoints on one port (a duplicate bind)
  *and* the management port classified as ingress — the exact confusion the endpoint split exists to
  prevent, where an unrouted host stops reaching the UI. The projection drops that one endpoint, leaves
  the other alone and warns once through `ProxyIngressWarnings`, which buffers until the host's logger
  exists because the projection is built before the container is.
- **A host with no `Kestrel:Endpoints:Http` gets its hosting URL promoted into one.** Kestrel binds
  `ASPNETCORE_URLS` / `UseUrls` only while *no* endpoint is configured; the moment ingress adds one they
  are overridden with a warning. A bare `dotnet run`, systemd or Aspire host that enabled the proxy
  would therefore lose its management listener at the next start. When — and only when — the projection
  derives ingress, the first plain-HTTP hosting URL is projected as `Endpoints:Http:Url`. With the proxy
  off nothing is projected at all, so development is untouched.
- **Every value read out of configuration is treated as possibly wrong.** An unparseable
  `Proxy:Enabled` is `false` and an unparseable port is *off* rather than the shipped default: this runs
  before the host exists, so a typo would otherwise be a stack trace at startup, and binding a public
  port the operator did not name is the worse way to read one.
- **`ProxyHttpsEndpoint.Configure` is unconditional.** `UseKestrelHttpsConfiguration()` is registered
  even when nothing is bound (`CreateSlimBuilder` omits it, and the loader refuses an `https://`
  endpoint before reaching our callback — it has to be in place before the reload that adds one), and
  the loader replaces Kestrel's default one even when the projection carries no endpoints at all.
- **`YarpListenerState` publishes an immutable snapshot.** Four facts that used to be written once at
  startup now change together at runtime, and a request judged by the new ingress ports and the old
  management port would be judged by a state that never existed. One volatile swap, read once per
  request.
- **The state is configuration truth, and the `ApplicationStarted` narrowing is gone.** It used to seed
  from configuration and then narrow to the addresses the server reported. There is no "the addresses
  changed" event to hang that on any more, and configuration is the wider — therefore safe — reading:
  no request can arrive on a port nothing is listening to, whereas a bound port missing from
  `IngressPorts` is the dispatcher's fall-through rule in force on a public port. A bind that fails is
  Kestrel's log line.

### What the spike showed

A real Kestrel on loopback, driven through the real projection with an in-memory stand-in for the
settings store (`ProxyIngressEndpointReloadTests`):

- with the proxy off, only the management endpoint accepts connections;
- flipping `Proxy:Enabled` brings both ingress ports up within a second, the TLS one served by our SNI
  callback (a real handshake against a generated chain);
- switching `Provider` to `caddy` takes both down again while the management endpoint never stops
  serving — which is what keeps the Settings page that made the change reachable;
- **the named endpoint's callback runs again on every re-add**, so the certificate store really is
  re-attached to the new listener (asserted with a counter, and by a second handshake);
- **a request already in flight on a listener being unbound completes.** The endpoint stops accepting
  new connections; the ones it has drain. Turning the proxy off does not cut off responses mid-write;
- handing Kestrel a section with no `Endpoints` block does **not** suppress the hosting URLs, so
  `ASPNETCORE_URLS`, launchSettings and Aspire bind exactly as they did.

### Consequences

- **Breaking:** `Kestrel__Endpoints__ProxyHttp__Url` and `Kestrel__Endpoints__ProxyHttps__Url` are no
  longer the knob. They are gone from the image and ignored where an operator still sets them. The
  ports are `WATCHTOWER__PROXY__YARP__HTTPPORT` / `__HTTPSPORT`, or the Settings page. Published host
  ports are unchanged for anyone on the defaults (`80:8081`, `443:8443`).
- Enabling, disabling or switching the provider binds and unbinds the ingress listeners with no
  restart. "HTTPS listener not bound — restart needed" is gone from the UI and the docs.
- A Caddy or Cloudflare deployment carries no ingress listener at all.
- **A bind failure is fatal at startup and survivable on reload, and the asymmetry is a trap.** At
  startup Kestrel rethrows (`IOException: Failed to bind to address …`) and the process exits — in
  Docker, a crash-loop. On a reload it logs at Critical, **keeps the endpoints it already had** and
  carries on. So moving an ingress port at runtime onto a port something else holds looks successful:
  the Settings page reports the new port, traffic keeps arriving on the old one, and the instance
  crash-loops at the next restart or self-update. `GetProxyStatus.ProviderDetail` therefore compares
  `IngressPorts` against Kestrel's `IServerAddressesFeature` and says `ingress port {n} failed to bind
  — see the logs`. That comparison is **diagnostics only**: the dispatcher keeps acting on the
  configured set, because a configured-but-unbound port costs nothing (no request can arrive on it)
  while a bound port missing from `IngressPorts` would put the management plane on a public listener.
  The docs tell operators to check the status and the log after changing a port.
- **The dispatcher classifies by exclusion, and therefore fails closed.** Once the management port is
  known, a connection is ingress unless it arrived *on that port* — rather than ingress only if its port
  is in the configured set. The configured set is kept for status and diagnostics, and as the fallback
  where no management port was ever derived (`TestServer`, the unit hosts). One rule precedes both: with
  **no ingress configured at all** nothing is ingress, which is the single-listener shape — the proxy
  off, both ports off, Caddy, Cloudflare, every development host — where refusing hosts would refuse
  them everywhere.
  - This is what makes a *stale* listener safe. A failed rebind leaves the old ingress port bound and
    serving under a configuration that has moved on; set membership would have called that port
    management and served Watchtower's own UI on it to anyone who found it. Exclusion keeps it ingress:
    unknown hosts get a 404, routed hosts are forwarded, Watchtower's own routes are still served.
  - The cost is that **any other endpoint an operator adds to `Kestrel:Endpoints:*` counts as ingress**
    while the proxy is on: unknown hosts 404 there and only routed hosts and Watchtower's own hostnames
    are served. That is the safe direction — a listener that serves less than intended, rather than one
    that serves the management plane to the internet. An operator who wants a second management listener
    should move `Kestrel__Endpoints__Http__Url` instead of adding one.
- `Kestrel__Endpoints__Http__Url` still owns the management port, and `ASPNETCORE_URLS` still does not
  apply to the shipped image.
