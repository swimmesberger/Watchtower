# ADR-0033: A route can be bound to a port, and Watchtower is its own CA for those

- Status: Accepted
- Date: 2026-08-31
- Related: [ADR-0022](0022-in-process-yarp-proxy.md) (the in-process proxy this extends),
  [ADR-0023](0023-login-hosts-are-watchtower-self-routes.md) (the other axis of a route — what it
  targets, where this adds how it is addressed),
  [ADR-0024](0024-postgresql-only-and-state-in-the-database.md) (certificates and the signing key are
  rows, which is why the CA is one too),
  [docs/reverse-proxy/README.md](../reverse-proxy/README.md#port-routes-https-on-a-lan-with-any-provider)
  (the operator guide, moved there by the addendum below).

## Context

The reverse proxy assumes a public domain. ADR-0022 built it around one: ingress terminates TLS on a
shared 443, picks the certificate from SNI, looks the `Host` up in the route table and forwards. ACME
issues the certificate, and ACME issues for names that resolve on the public internet.

The workloads Watchtower actually targets are not all like that. A NAS on `nas.lan` at `192.168.1.10`,
with no domain, no public DNS and no port 80 reachable from a CA, still wants the good half of what the
proxy does: a stack's service reachable **only through the proxy**, with no `ports:` in its own compose
file. That half already worked and needed nothing new — the service container joins
`watchtower-ingress-{stackId}` and the proxy dials `{project}-{service}:{port}` over it — so what was
missing was not connectivity but an address, and TLS on that address.

Three constraints then decide the shape.

**ACME cannot issue.** No public CA will sign for `nas.lan` or for `192.168.1.10`; HTTP-01 needs a
challenge the CA can fetch over the internet, and there is no internet leg. ADR-0022 named the two
structural exits from the rate-limit problem, and one of them — an on-premises CA that speaks RFC 8555,
step-ca — already works here. It is also a second server to install, configure and keep running, which
is not what a hobby deployment on one box is asking for.

**Bare-IP and LAN-hostname clients send no usable SNI.** A browser dialling `https://192.168.1.10/`
sends no `server_name` extension at all, and one dialling a single-label `.lan` name sends a value no
certificate for a public domain matches. The whole ADR-0022 ingress mechanism — one TLS port,
disambiguated per connection by SNI — has nothing to disambiguate with. Whatever identifies the service
has to be something the connection carries before any name is spoken, and on a LAN the only such thing
is the port it arrived on.

**The `Host` header is not evidence.** Even after the handshake, a client reaching a bare address writes
whatever it likes in `Host`. A design that fell back to it would let a request on one service's address
name another service's routed domain and be forwarded there, past the access check.

So: one dedicated TLS port per service, a certificate that names the LAN addresses, and a trust anchor
the operator installs once by hand. That last part is the honest cost of the feature, and it is why the
decision is not "generate something so the browser stops complaining".

## Decision

### 1. A route is addressed by a domain *or* by a port, and the shape of a port route is structural

`Route.Binding ∈ {Domain, Port}`, defaulted to `Domain` and immutable after creation — for the reason
`Target` is (ADR-0023): the two kinds fill different columns, and flipping one in place would move a
live address rather than edit it. `Domain` becomes nullable, `ListenPort` is added, and each gets a
**filtered unique index** (`WHERE "domain" IS NOT NULL`, `WHERE "listen_port" IS NOT NULL`) so
uniqueness holds over the rows that have an address of that kind and says nothing about the rest.

The rest of the shape is a check constraint rather than handler etiquette:

```sql
ck_routes_binding:
  ("binding" = 'Domain' AND "domain" IS NOT NULL AND "listen_port" IS NULL)
  OR ("binding" = 'Port' AND "domain" IS NULL AND "listen_port" BETWEEN 1 AND 65535
      AND "target" = 'Service' AND "access_mode" = 'Public' AND "tls_enabled")
```

Each clause of the `Port` half pays for itself downstream. **Service target**: Watchtower is already
served on its management port, and a second in-process listener for it would be a management plane on a
number nobody bound privately. **Public**: forward-auth redirects an anonymous visitor to a login page
and back *to the address they came from*, and a bare `host:port` is not an address the central login can
return anyone to — so the dispatcher can skip `AccessVerifier` on this path knowing the database will
not store the case it would have to handle. **TLS on**: a plain-HTTP port route would be a second,
weaker way into an application that has a working one. Making these structural is what lets
`ProxyPortSite` and `ProxyPortRouteSnapshot` be far smaller records than their host-shaped counterparts
instead of carrying five fields that are always the same value.

Port routes are **yarp only**. `CaddyManager` and `CloudflareTunnelProvider` set them to `Error` with
one shared constant, `ProxySiteProjection.PortRouteUnsupported` — the ADR-0023 pattern, for the same
reason: a sibling container and a tunnel have no way to lend Watchtower a listener on Watchtower's own
host, and a row that is silently skipped sits at `Pending` forever with the reason nowhere.

> **Superseded** by the [addendum](#addendum-2026-09-01-port-routes-are-provider-independent) below.
> The premise was wrong: nobody was being asked to lend Watchtower a listener, because the listener was
> always Watchtower's own. Port routes are gated on `Proxy:Enabled` alone and the constant is gone. The
> paragraph is left as written — it is what the rest of this decision was reasoned against.

### 2. One dedicated HTTPS listener per port route, bound at runtime

The listeners reach Kestrel the way the ingress ports already do (ADR-0022's addendum). An **internal**
setting, `Watchtower:Proxy:Yarp:PortRoutePorts`, carries the ports;
`ProxyIngressKestrelConfiguration.Project` reads it and emits one `Endpoints:ProxyPort{n}:Url =
https://+:{n}` per port; the existing `reloadOnChange: true` machinery binds and unbinds them with no
restart. `Endpoints:ProxyPort*` keys an operator writes are masked, like `ProxyHttp*`.

The setting is written by `YarpProxyProvider.ApplyAsync`, compare-then-write, and that placement is the
point rather than a convenience: route creation, route deletion, an update that moves a port, a stack
delete cascading its routes away and the cross-instance change signal all arrive at that one method, so
writing it there is what makes "the rows say so" and "a socket is bound" the same statement. The
comparison is between two `PortRouteListeners.Format` renderings — canonical, sorted, deduplicated — so
a converged instance writes nothing, and a value stored in some other spelling is not rewritten forever.
Every instance runs the projection on every pass; an unconditional write would be a settings write per
pass per instance for a value nobody moved.

`PortRouteListeners.Parse` is deliberately forgiving and cannot throw: it runs inside the projection,
which is built before the host exists, where ADR-0022 already established that a value that cannot be
read means a listener that stays off rather than a stack trace at startup.

### 3. TLS comes from `ConfigureEndpointDefaults`, claiming listeners by port

The endpoints are named `ProxyPort{n}` and they come and go with the routes, so there is no fixed set of
names against which a per-endpoint callback could be registered up front. Kestrel's **endpoint
defaults** are the way out: `ConfigureEndpointDefaults` runs for every listener as it is created, before
the configuration loader does anything about TLS, and the loader's own https handling is guarded on
`!listenOptions.IsTls` — which `UseHttps(TlsHandshakeCallbackOptions)` sets. So a listener claimed there
is TLS on our terms and the loader steps over it, exactly as it steps over the named `ProxyHttps`
endpoint. A config endpoint with an `https://` URL and no `Certificate` section therefore binds without
one. The scope is `listen.IPEndPoint.Port`: decided once as the listener is created rather than per
connection, because the port is a constant for that listener's life and is the only thing a bare-address
client gives us.

**The first attempt was `ConfigureHttpsDefaults` with a `ServerCertificateSelector`, and it was wrong.**
Installing a selector makes Kestrel's `HasServerCertificateOrSelector` true for **every** HTTPS listener
in the process, which has two effects that have nothing to do with port routes: an endpoint's own
`ServerCertificate` is discarded in the selector's favour, and `ApplyDefaultCertificate` — the
`Kestrel:Certificates:Default` and development-certificate fallback — is skipped. An operator's own
HTTPS endpoint would then have stopped serving at the next rebind, silently, on a deployment whose only
change was adding a port route. This was found empirically during implementation and is now held by two
tests (`AnOperatorsHttpsEndpoint_KeepsItsOwnCertificate`,
`AnHttpsEndpointOnTheDefaultCertificate_KeepsServing`). Claiming listeners individually by port cannot
reach a port no route owns.

The hook itself is the same `TlsHandshakeCallbackOptions` the SNI endpoint uses, serving the prebuilt
`SslStreamCertificateContext` for the shared LAN leaf, with ALPN offering h2 and HTTP/1.1. When no leaf
is held it throws an `AuthenticationException` — Kestrel's ordinary failed handshake, logged at Debug —
and logs one warning per port for as long as the condition lasts, cleared again when that port serves a
handshake, so a scanner cannot fill the log.

### 4. The port set that decides a listener is read from the projected section, not from `YarpListenerState`

Two things watch the projected Kestrel section: Kestrel's own loader, and
`ProxyListenerStateInitializer`, which republishes the snapshot the dispatcher reads. Nothing orders
them, and measured, the loader's runs first. A listener created during a reload would therefore consult
a state that has not caught up and be built without TLS until something rebound it.

So `PortRouteListeners.BoundPorts(section)` is the **single definition** of "which ports carry a port
route's listener", and both the TLS hook and the listener-state derivation read it. It reads the
endpoints' URLs out of the section rather than the setting they came from, which matters because the
projection **drops** a port that collides with the management or an ingress port — a dropped port is
absent from the section, so nothing downstream can reinstate it.

The request path asks the same question, and asks it on every request, so `ProxyIngressSection` memoises
the answer as a `FrozenSet` and invalidates it on the section's reload token. The dispatcher's fast path
is the cached `YarpListenerSnapshot.PortRoutePorts`; the section is consulted as well, unconditionally,
because that snapshot can lag. The two compose the right way round. The snapshot is post-projection
truth, so it is right to ask first — a route sitting on the ingress HTTPS port (reachable if
`Yarp:HttpsPort` moves by environment underneath a create-time validation that already passed) must not
capture port 443. And the section is post-projection truth as well, so consulting it cannot reinstate a
collision-dropped port; what it can do is close the window where the socket is bound and no cached
reading knows it. On a port-routes-only deployment that window is not academic: a stale snapshot carries
no ingress ports at all, `IsIngress` is then false, and the request would fall through to Watchtower's
own SPA on a port published to the LAN.

### 5. Dispatch is by local port, ahead of the host lookup

On a port route's listener the `Host` header decides nothing, so it is not consulted: a client writes
whatever it likes there, and letting it name a routed domain would turn one service's dedicated port
into a way into another's. `YarpHostDispatchMiddleware` therefore branches on the local port before the
host lookup. A port that is a port route's listener but whose row is not in the table — a deletion a
moment ago, the socket not yet unbound — gets the same bare 404 an unrouted host gets on ingress, rather
than falling through to the host path.

What the branch does: strip the full `IdentityForwarding.StripHeaderNames` set the host path strips —
every name in every identity vocabulary the proxy can emit, `Remote-*`, `X-Auth-Request-*`,
`X-Forwarded-User`/`-Email`/`-Groups`/`-PreferredUsername`, the Cloudflare Access pair and
`X-Watchtower-*` — plus `X-Forwarded-Method` and `X-Forwarded-Uri`; lift Kestrel's 30 MB body cap
because the upstream is entitled to an opinion about its own uploads; and forward to
`http://{project}-{service}:{port}` over the stack's ingress network with `X-Forwarded-Host` echoed from
the request, `X-Forwarded-Proto: https` and no identity headers at all.

What it deliberately does not do is the interesting half, and each omission is something the check
constraint decided. **No access check** — the row is `Public` or it is not stored. **No
`/.watchtower/*`** — those paths exist so an anonymous visitor can be handed a session on a protected
app's own domain, nothing redirects anyone here, so the prefix is not reserved on this listener and an
upstream that uses it keeps it. **No HTTPS upgrade** — the listener is TLS, there is no plain-HTTP leg
to redirect from, and the redirect would have to be rebuilt out of a hostname this route has not got.

**ACME challenges are not answered on a port listener.** "Every host the process serves" is what makes
`AcmeChallengeMiddleware` correct on the shared ingress ports, where a CA may arrive for any domain. A
port route's listener serves exactly one upstream over TLS on an address no CA validates; answering
there would hold a path the upstream is entitled to serve itself, for a challenge that could never have
been aimed at that address.

### 6. Watchtower is its own certificate authority for these routes

A new table, `internal_cas`, holds one row: an ECDSA P-256 self-signed root, `CN=Watchtower Internal
CA`, valid ten years, `BasicConstraints(CA, pathLen 0, critical)`, `KeyUsage(KeyCertSign | CrlSign)`,
with a subject key identifier. Its private key is stored under `KeyProtector` like the ACME account key
and the certificates (ADR-0024), and the row is created the way `AcmeAccountStore` creates its account —
insert unconditionally, let the unique index on `name` settle the race, re-read the winner — because two
instances starting together would otherwise end up with two roots, of which an operator can only have
imported one. Ten years because replacing it costs a manual import on every client that trusts it; an
expiry an operator has to notice and act on is exactly what this must not have. There is no rotation in
v1: replacing the row by hand is the escape hatch, and the next issuance follows it, because a leaf is
reissued whenever its authority key identifier stops naming the stored root.

From it, **one shared leaf serves every port route**. Not one per route: a client reaching a bare
address sends no usable SNI, so the listener cannot pick between certificates anyway — what it can do is
present one that names every address the operator said this deployment answers on. The leaf is held
under the store key `internal-lan.watchtower.invalid` (RFC 6761 reserves `.invalid`, so it can neither
collide with a routed domain nor be issued for by a public CA), carries the LAN names from the global
`Watchtower:Proxy:Yarp:LanNames` setting as DNS and IP subject alternative names — both forms, because a
browser asked for a name matches only a DNS SAN and one asked for a bare address matches only an IP SAN
— and carries the **serverAuth** extended key usage, which is not decoration: Kestrel's
`EnsureCertificateIsAllowedForServerAuth` refuses a certificate whose EKU omits it, so a leaf without it
would be rejected before any client saw it. There is deliberately **no AIA and no CRL distribution
point**: both are URLs a client would fetch mid-handshake, and there is nothing on a LAN to serve them,
so leaving them out keeps chain building purely local. Validity is a year, renewed by the ordinary
`CertificateRenewalPolicy` — the last third of the lifetime, the same fraction every ACME certificate
uses. `CertificateStore.PruneUndesiredAsync` excludes `Source = internal-ca`, since nothing routes to
that store key and the leaf would otherwise look undesired to every pass and be deleted thirty days
after expiry.

`InternalCertificateService.EnsureAsync` is idempotent and cheap — it decides whether the held leaf
still says what it should and usually does nothing — which is what lets it be called from three places
that know nothing about each other: the startup state initializer (before anything is served), the tail
of `YarpProxyProvider.ApplyAsync` (so a route created a second ago works immediately), and
`CertificateManager`'s five-minute reconcile. It reissues when nothing is held, when the LAN names no
longer match exactly, when the issuing CA changed, or when renewal is due, and it writes the outcome
onto the port routes' rows through `RouteStatusUpdater`.

Two gates that apply to ACME issuance deliberately do not apply here. **Not the `acme-issuer` lease**
(ADR-0024): that lease protects a rate-limited remote resource, while issuing here is local, free,
idempotent and resolved by a unique index when two instances race — and the instance an operator is
talking to has to be able to make the route they just created work, rather than wait for whichever node
holds a lease that exists for a different reason. **Not `HttpsBound`**: a deployment that serves nothing
but port routes runs with the HTTPS ingress port off, and gating on it would mean exactly that
deployment never got a certificate.

The root is downloadable at `GET /api/proxy/internal-ca.crt` — PEM by default, `?format=der` under a
`.cer` name for the import dialogs that decide what they are looking at from the extension — behind the
same management-plane session requirement as the volume download. The certificate carries no secret, but
who is asking still says something about the deployment, and the only caller is an operator's browser.
The endpoint never *creates* the CA: a request against a deployment that has never needed one is a 404,
not the moment a root springs into existence.

Two things worth separating, because both are supported and they are not the same. ADR-0022 decision 5
is about **talking to** an internal CA: point `AcmeDirectoryUrl` at a step-ca, add its root to the
bundle, and every ordinary domain route gets a certificate from it over RFC 8555. This decision is about
**being** one, for LAN addresses no ACME CA of any kind will issue for. They compose — a deployment can
do both — and neither replaces the other.

ADR-0022 also rejected a self-signed **placeholder** for ACME hosts, and this is not a reversal of that.
The placeholder was to be presented automatically, for a domain that was about to get a real
certificate, to a visitor who would then be taught to click through a browser warning that resolves
itself minutes later. This is chosen: the operator sets the LAN names, creates a port route, downloads a
trust anchor and installs it. Nobody is asked to click through anything, and what is trusted afterwards
is a root the operator put there.

### 7. Publishing the host port reuses the self-update coordinator

A port route needs a second thing to be reachable: the same port published on Watchtower's own
container. Docker cannot add a binding to a running container. Watchtower already recreates its own
container for the self-update — `CoordinatorMode` plus `ContainerCloneSpec`, which clones the config
onto an image, stops, renames aside, creates, starts and rolls back on failure, with no compose file
involved — so that is what publishes ports too.

`ContainerCloneSpec.FromInspect` takes optional `PortAmendments(Publish, Unpublish)`, merging
`{port}/tcp` into both `Config.ExposedPorts` and `HostConfig.PortBindings` (host port equal to container
port, since a port route's listener is inside the container on the number the operator types in the
browser) and removing unpublished ones. Both halves are written because the daemon accepts a binding for
a port that is not exposed, and `docker inspect` and every UI would then disagree about what the
container offers — and the next clone would carry that disagreement forward. It stays a pure JSON
transformation, so the rule that decides whether an operator's binding can be taken away is unit-tested
without a daemon. `CoordinatorMode` gains `--publish-ports` / `--unpublish-ports`; the rest of the flow
is byte-for-byte the self-update's.

It is **operator-initiated and confirmed, never automatic**: `proxy.applyPortBindings` is what the
Routes page's confirmation dialog calls, because this restarts the management plane. The handler answers
before the restart lands — the coordinator's three-second delay exists for exactly that — and the whole
set of pending ports is batched into one recreate.

Only ports Watchtower published are ever taken away, and that is what
`Watchtower:Proxy:Yarp:ManagedHostPorts` is for. The plan is three rules over three sets: publish what
is wanted and not bound; unpublish only what is *both* claimed and bound and no longer wanted; carry
forward the claims that are still true. So a port the operator published themselves satisfies its route
as it is — it is not republished, and, importantly, not adopted either, which means it is never removed.
The claim is written **before** the coordinator is spawned, because the coordinator ends this process
and there is no "after"; that is safe in the direction that matters, since a claim is only ever acted on
for a port that is also currently bound, and `StartAsync` prunes the set to `managed ∩ bound` on every
start.

The two recreate paths keep **separate runtime records** — `self.runtime` is a self-update's state, down
to a stage named "pulling" and an error about an image, and a port publish writing into it would
mislabel itself and stamp on an update genuinely in flight — and therefore **cross-refuse**. Each reads
the record it does not own before spawning (`CoordinatorContainers.OtherRecreateInFlightAsync`). Without
that, two coordinators seconds apart would each sleep three seconds and then stop, rename aside and
recreate the *same* container id; the loser acts on a container the winner has already renamed, and its
stop is the one step outside the coordinator's try block, so it dies with no rollback. Reading it
through the settings store rather than an in-process flag also makes the refusal correct for a
coordinator a *previous* process instance left running.

## Worked example

A `nas.lan` box at `192.168.1.10`, no domain, the `yarp` provider, authentication on, one stack `media`
with a service `web` on container port 8080, and no public ingress at all
(`Yarp:HttpPort = 0`, `Yarp:HttpsPort = 0`).

| id | binding | address | target | stack / service:port | access | served as |
|---|---|---|---|---|---|---|
| 1 | Port | `:9001` | Service | `media` / `web:8080` | Public (enforced) | dedicated TLS listener, internal-CA leaf |

Settings: `Proxy:Enabled=true`, `Provider=yarp`, `Proxy:Yarp:LanNames = nas.lan, 192.168.1.10`. Ports on
the Watchtower container: `127.0.0.1:8080:8080` (management, private) and `9001:9001` — the second one
added by the confirmed in-app publish, which recreated the container in about five seconds.

What happens, in order:

- Saving the LAN names writes `Proxy:Yarp:LanNames`. Nothing is issued yet: with no port route,
  `InternalCertificateService` returns without even creating a CA.
- Creating route 1 refuses immediately if the LAN names are empty, if 9001 is the management or an
  ingress port, or if another route already holds it. It succeeds, `ApplyAsync` writes
  `PortRoutePorts = 9001`, the projection emits `Endpoints:ProxyPort9001:Url = https://+:9001`, Kestrel
  binds it, and the endpoint-defaults hook claims it as TLS.
- The same `ApplyAsync` calls `EnsureAsync`, which creates the CA row and issues one leaf,
  `CN=internal-lan.watchtower.invalid`, SANs `DNS:nas.lan` and `IP:192.168.1.10`. Route 1 goes `Active`
  with the leaf's expiry.
- The Routes page shows *host port 9001 is not published*, because it is not. One confirmation later
  the coordinator recreates the container with `-p 9001:9001` and Watchtower comes back.
- The operator downloads `/api/proxy/internal-ca.crt` and imports it on their laptop and phone.
- `https://nas.lan:9001/` → the listener is 9001's → route 1 → identity headers stripped → forwarded to
  `media-web:8080`. `https://192.168.1.10:9001/` does the same, matching the IP SAN.
- `https://nas.lan:9001/` with `Host: someone-elses-app.example.com` → still route 1. The header decides
  nothing here.
- `http://nas.lan:8080/` → the management UI, on the port bound privately.

Adding `nas.local` to the LAN names later reissues the one leaf with three SANs; the port listener is
untouched, and nobody re-imports anything, because the root did not change.

## Consequences

- **The operator has to publish the port on the Watchtower container, and that is a restart.** The
  in-app flow makes it one confirmed recreate of a few seconds rather than an edit and a manual
  `docker` invocation, but it is still a restart of the management plane, which is why it is a button
  behind a dialog and never something that just happens. A manual `-p 9001:9001` remains the fallback,
  and is the only route on a bare-process install or a multi-instance deployment, where the flow refuses
  itself.
- **Compose drift.** A later `docker compose up -d` recreates the container from the compose file and
  drops whatever the coordinator added. The startup reconcile notices — the claims are pruned to what is
  actually bound — so the routes report "not published" again and the button reappears, which is the
  correct behaviour rather than a silent belief that the work was done. Compose-managed installs should
  mirror applied ports into their compose file. This drift already existed conceptually for the
  self-update; port routes make it visible on a page.
- **Image-tag drift on a ports-only recreate.** The coordinator is handed the container's *configured*
  image reference, which is a tag, so a tag that moved locally since this process started brings the
  newer image along with the port change. Passing the resolved image id instead would fix that and break
  something worse: a `sha256:` in the clone's `Config.Image` would defeat the self-update's digest
  comparison from then on. The recreate is faithful in every other respect, and this is written down
  rather than worked around.
- **A wedged coordinator blocks both recreate paths indefinitely, deliberately.** The cross-guard is not
  cleared on a timeout, because letting a second coordinator start while the first may still be
  genuinely mid-recreate is precisely the hazard it exists to close. What can be improved is the
  operator's position, so the refusal names which path started it, names the coordinator container, and
  says to remove that container and restart Watchtower if it is stuck.
- **`ManagedHostPorts` is a Global setting, and on a cluster that is a real limitation.** The refusal
  that keeps this feature single-instance is one-directional: the only cheap evidence of a second
  instance is the `acme-issuer` lease being held by somebody else, which proves a second instance exists
  while the converse proves nothing. So on a cluster where this node happens to hold the lease the
  button is offered, publishes on this node's container only, and every *other* node's startup prune
  then strips claims its own container does not honour. The consequence is that ports node A published
  become permanently un-removable rather than merely un-offered — fail-safe, since nothing is ever
  removed wrongly, but wrong. Publish the ports by hand on every node instead; a real instance registry
  is a feature of its own.
- **Port-binding state is its own RPC, `proxy.getPortBindings`, not `Route.StatusDetail`.** Folding it
  into the route status would put a Docker inspect behind a badge that every page showing the proxy
  status polls. The cost is that route status can no longer express it, and in particular cannot express
  *unknown* — so the UI carries that state explicitly: a container Watchtower cannot see (a bare-process
  install, or a momentarily unreadable Docker socket) is reported as "Watchtower cannot see its own
  container", with the manual instructions, and never as "not published". An unadorned row beats one
  accused of being broken.
- **Adding or removing a port route rebinds Kestrel, and the named TLS ingress listener refuses a
  connection or two while it is rebuilt.** The projected key set really changed, so the loader really
  reloads; the reload tests retry their handshakes for this reason. It is a blip on a route change, not
  on an unrelated settings write — the projection still raises its own token only when the projected
  keys moved.
- **An operator's own HTTPS endpoint with no certificate anywhere behaves exactly as it did before this
  feature** — the host fails to start with "no server certificate was specified" where no development
  certificate exists, and serves the development certificate where one does. Worth stating because the
  first mechanism silently changed it into a third thing: a host that starts and then refuses every
  handshake on a listener the operator believes is configured.
- **An env-pinned ingress port colliding with an existing port route is not refused.**
  `proxy.updateConfig` checks both directions — a new listen port against the stored ingress ports at
  create time, a new ingress port against the stored listen ports at save time — so whichever is written
  *through the settings store* second is the one refused, and neither order reaches the collision. A
  value supplied through the environment never passes through that handler at all (ADR-0014). The
  projection then drops the port-route listener with a warning while the row stays `Active`, which is
  the safe direction (nothing is bound that should not be) and a discrepancy an operator has to read the
  log to understand.
- **A startup bind failure on a port-route port is still fatal**, and ADR-0022's asymmetry is unchanged:
  fatal at startup, survivable-but-stale on a reload. Create-time validation refuses a listen port that
  collides with Watchtower's own ports, but nothing can refuse an unrelated host process holding 9001 on
  bare metal, and that still wedges startup.
- **A listen port another container already publishes is refused, naming that container.** Asked of the
  Docker daemon at route creation, at an edit that moves the port, and again before the publish recreates
  this container — the message carries the container's name and its compose project and service, because
  the alternative is a recreate the daemon refuses to allocate the port to, which rolls back and reports
  "host port not published" with nothing pointing at what holds it. Note that this is *not* the bind
  conflict above: in a bridge-networked deployment Kestrel binds inside its own namespace and cannot
  collide with another container at all; what collides is the host-port allocation, and the loser is
  whichever container is started second. Containers in any state count (a stopped desired-state stack
  comes back) and only TCP does. The check fails open in both directions — an unreachable daemon refuses
  nothing, and so does a self whose container id cannot be resolved, since Watchtower's own published
  port is the state the feature is trying to reach and a false refusal naming it is worse than a missed
  one. That is also why self is resolved as `HOSTNAME` → inspect → the authoritative id and matched
  exactly: `HOSTNAME` is a custom name whenever the container carries one, and a prefix match on it
  refused the operator's own binding. What this deliberately does *not* do is inspect a routed service's
  `ports:` on the deploy path — a stack is still free to publish the ingress or management ports, and the
  collision surfaces when one of the two containers next starts. That belongs to
  [ADR-0029](0029-blue-green-stack-deploys.md), which is still Proposed; the contract itself is written
  down in [docs/reverse-proxy/README.md](../reverse-proxy/README.md#what-a-routed-stack-must-not-do).
- **Losing `KeyProtectionSecret` is no longer a self-healing failure.** ADR-0024 could say that losing
  it invalidates sessions and forces every certificate to be reissued — a blast radius that resolves
  itself, because ACME simply orders again. The CA key breaks that property: decision 6 treats an
  unreadable one as fatal to issuance and refuses to mint a replacement root, since minting one would
  abandon the anchor every LAN client was told to trust and the failure would then surface on those
  clients rather than on the server. So issuance stops and the port routes go to `Error` until a human
  either restores the secret or deletes the `internal_cas` row — and the second option costs an import
  on every device that trusted the old root. That is the right trade (a silent re-mint would break the
  same devices, without telling anyone), and it means the key-protection secret is now backup material
  with a manual recovery step behind it. The operator documentation says so in all three places it
  describes the secret.
- **Rolling back past this ADR requires deleting the port routes first, and Caddy is where that bites.**
  A domain-less route row is a shape no earlier build knows: the pre-0033 `ProxySiteProjection` reads
  `Domain` off every row unconditionally and emits a site with none, which `CaddyConfigBuilder` then
  renders as a block with no address — and a Caddyfile Caddy refuses is refused whole, so one unserved
  route stops every domain that was working. Nothing gates creation on the provider either (a port route
  under Caddy or Cloudflare is marked `Error` as unsupported and left in the table), so the deployment
  most exposed to this is one that never served a port route at all. Gating creation would not fix the
  rollback and would cost the ability to prepare routes before switching provider, so this is documented
  in `docs/upgrading.md` rather than enforced.
- **Certificate scope is narrow on purpose.** The internal CA serves port routes and nothing else;
  domain routes stay on ACME, and the internal leaf's store key never enters the ACME desired set. The
  seam for a per-route certificate source — an uploaded certificate, a second CA — is left open by the
  `Source` column, and is not this ADR.
- **LAN names are global.** Every port-route certificate carries all of them, and adding one reissues
  the single shared leaf for every route at once. That is the right shape while the certificate is
  selected by port rather than by name, and it does mean a service's certificate names addresses that
  have nothing to do with it.
- **A note for a future ADR-0022 revisit**, found while reasoning about the dispatcher: `IsIngress`
  short-circuits to `false` when `IngressPorts` is empty, and that check *precedes* the
  management-port exclusion rule. So a listener state that was never derived from a real projection
  reads every port as non-ingress rather than fails closed. It is pre-existing and reachable only where
  the state is a default (`TestServer`, the unit hosts) or has not caught up. The port-route path no
  longer depends on it — decision 4's second reading is what removed that dependency — so this is
  recorded here rather than fixed as a side effect of this work.

## Addendum (2026-09-01): port routes are provider-independent

### Context

Decision 1 ends with three sentences that were never true. A port route was gated on
`Provider == yarp` everywhere — `DerivePortRoutePorts` returned nothing under another provider so no
listener was projected, `InternalCertificateService` returned on the same gate so no certificate was
issued, the whole port plane lived inside `YarpProxyProvider` and therefore never ran, and Caddy and
Cloudflare marked the rows `Error: PortRouteUnsupported`. The Routes page hid the Port binding unless
`status.provider === 'yarp'`.

The reason given was that "a sibling container and a tunnel have no way to lend Watchtower a listener on
Watchtower's own host". Read again, that sentence answers itself: nothing was being lent. A port route is
a socket Watchtower binds inside its own container and forwards over the stack's ingress network. Which
backend terminates the *public domains* has no bearing on it — and the deployments most likely to have no
domain at all are exactly the ones behind a Cloudflare Tunnel (a NAS on a CGNAT connection) or on a
legacy Caddy install. The gate excluded the users the feature was designed for.

The conflation was written into the setting names as well: `Proxy:Yarp:LanNames`,
`Proxy:Yarp:PortRoutePorts`, `Proxy:Yarp:ManagedHostPorts`.

### Decision

**Port routes are gated on `Proxy:Enabled` and on nothing else, under every provider.**

**1. A Watchtower-native port-route plane.** `Services/PortRoutes/PortRoutePlane` is a singleton hosted
service registered *alongside* the three providers rather than among them. It is not an `IProxyProvider`
and is not selected by `Proxy:Provider` — there is nothing to select between. It owns everything a port
route needs, which is four things that are one statement made in four places: the port half of the route
table (`ProxySiteProjection.ProjectPortRoutes`), the listener setting the Kestrel projection reads, the
internal certificate those listeners present, and Watchtower's own container's membership of the ingress
network of each port-routed stack. Its public surface is `ApplyAsync`, `ConnectStackAsync` and
`IHostedService`. Its shape is `YarpProxyProvider`'s — one transition lock, a background reconcile off
the startup path because joining networks talks to the Docker daemon, and its own `ProxyChangeSignal`
subscription, taken the same way the provider takes its own: two independent debounced watchers on one
key, rather than one watcher that has to know about both. `YarpProxyProvider` is domain routes only now,
with no forwarding shims left behind.

**2. The route table has two writers and one snapshot.** `ProxyRouteTable` was written wholesale by
`YarpProxyProvider`; under Caddy or Cloudflare that provider is inactive and never writes, so the port
plane has to own its half independently. `PublishHostRoutes(sites)` and `PublishPortRoutes(portSites)`
each recompute one combined immutable `ProxyRouteTableSnapshot` from both halves held internally and
volatile-swap it. The dispatcher still reads exactly one snapshot per request and knows nothing about the
split, and `TlsHosts` — the ACME desired set — derives from the host half alone, so a port route still
cannot enter it. When yarp is inactive its half is simply empty.

**3. The gates.** `ProxyIngressKestrelConfiguration.DerivePortRoutePorts` reads `Proxy:Enabled` (parsed
the same defensive way as before — this runs before the host exists) and not the provider;
`IsInProcessProxyActive` stays for the 8081/8443 endpoints. The projection's early return already handled
"no yarp ports, some port-route ports", which is the port-routes-only shape decision 4 was built around;
under Caddy the yarp ports are null, so of the two collision drops only the management-port one can fire.
`InternalCertificateService` is gated on `Proxy:Enabled` alone. `ProxyProviderRouter.ApplyAsync` and
`ConnectStackAsync` drive the selected provider and then the plane; `Enabled`, `IsRunningAsync` and
`ForgetDomainAsync` remain statements about the domain provider and stay with it. `PortRouteUnsupported`
and both call sites are deleted; `CloudflareTunnelProvider.SelfRouteUnsupported` is untouched.

**4. The settings are renamed, and a migration carries the stored values.**
`Watchtower:Proxy:PortRoutes:{LanNames, Ports, ManagedHostPorts}`, with `ProxyOptions.PortRoutes` binding
a new `PortRouteOptions` and `YarpProxyOptions.LanNames` deleted. `PortRouteSettingsMigration` mirrors
`ProxyProviderMigration`: it runs once from `Program.InitializeDatabaseAsync`, guarded by the sentinel
`Watchtower:Proxy:PortRoutes:Migrated`, copies each old key's **stored** value to the new key when the new
key is unset, logs, and audits under `proxy` / `config.migrate`. The old rows are left in place so a
rollback still finds them, which is also why the sentinel is load-bearing: without it every restart would
re-copy them over whatever the operator has since typed.

An environment pin on an old name cannot be carried across and is not silently dropped: env values never
enter the settings store, so `WATCHTOWER__PROXY__YARP__LANNAMES` simply stops mapping to anything, and it
is warned about on *every* start (not once behind the sentinel — the condition is a line in a compose file
that stays true until somebody edits it).

### Consequences

- **Watchtower's own container now joins the ingress network of a port-routed stack under any provider.**
  ADR-0022's exposure — Watchtower on the same L2 segment as a tenant's containers — is what Caddy and
  Cloudflare deployments were spared by not running the in-process proxy at all. It now reaches exactly
  the stacks they port-route, and no others: the plane's sweep and its `ConnectStackAsync` are both
  narrowed to `Binding == Port` rows, so a stack with only domain routes is untouched. A deployment with
  no port routes is unchanged in every respect. The double join under yarp costs nothing —
  `ConnectContainerAsync` reads the daemon's "endpoint already exists" 403 as success.
- **The renamed settings, and the env-pin caveat.** A stored value moves itself; a pinned one does not
  and cannot, and the deployment loses what it stated until the variable is renamed. This is the second
  place a settings rename has met ADR-0014's precedence order, and the answer is the same one: say so,
  loudly, on every start. `docs/upgrading.md` carries the table and the log line.
- **The ACME and ingress listeners remain yarp-only.** 8081, 8443, HTTP-01 and the SNI certificate map
  are the in-process provider's and are still gated on it. What moved is only the plane that was never
  its own to begin with. `GetProxyStatus.ProviderDetail` reflects the split: the ingress caveats are
  reported under yarp, the port-listener line under every provider.
- **A Caddy or Cloudflare deployment can now hold *served* port routes, which makes the ADR-0033 rollback
  hazard real for it.** Before this, such a deployment could only hold port routes it had never served;
  the pre-0033 projection's crash on a domain-less row was therefore a trap nobody had a reason to walk
  into. Now they are a working feature, and `docs/upgrading.md`'s "delete every port route before rolling
  back past ADR-0033" applies to those deployments in earnest.
- **`proxy.getConfig` grew a `portRoutes` block and `proxy.updateConfig` renamed one field**
  (`yarpLanNames` → `portRoutesLanNames`), which is a wire change in the generated client. The Settings
  page's LAN names moved out of the yarp block into their own "LAN port routes" section, and the Routes
  page's Certificates card split: the ACME table under yarp, the Internal CA block whenever the proxy is
  on.

## Rejected alternatives

- **A self-signed leaf per route.** Every route would then be its own trust anchor, and the operator's
  one-time import would become an import per service on every device. One CA, one import, and the leaves
  are then Watchtower's business.
- **One shared HTTPS port, multiplexed by SNI, like domain routes.** This is the mechanism that does not
  survive the premise: a client dialling a bare IP sends no `server_name` at all, and there is nothing
  else in a TLS ClientHello to route by. The `Host` header arrives too late and is attacker-controlled
  besides.
- **`ConfigureHttpsDefaults` with a `ServerCertificateSelector`** — decision 3's story. It is the
  smaller change and it looked right; it silently breaks every other HTTPS endpoint in the process.
- **Registering a callback per endpoint name late, with `loader.Endpoint(name, cb)`.** The names are not
  known until the routes are, so the registration would have to happen against a loader that is
  concurrently reloading — an unsynchronised race, with a listener that is TLS or not depending on which
  side won.
- **A pre-registered pool of endpoint names** (`ProxyPort1`…`ProxyPortN`) so the callbacks could be
  attached at startup. It works, and it puts an arbitrary ceiling on how many port routes a deployment
  may have, chosen at build time by nobody in particular.
- **Folding port-binding state into `Route.StatusDetail`.** It is genuinely the tidier model — one place
  a route says why it is not working — and it would put a Docker inspect behind the status badge that
  every page polls.
