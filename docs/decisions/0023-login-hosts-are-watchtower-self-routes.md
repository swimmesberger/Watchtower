# ADR-0023: Login hosts are Watchtower self-routes

- Status: Accepted
- Date: 2026-08-23
- Related: [ADR-0022](0022-in-process-yarp-proxy.md) (the in-process proxy this extends),
  [ADR-0015](0015-proxy-provider-abstraction.md) (the provider seam both ride on),
  [docs/central-auth/design.md](../central-auth/design.md) §11 (the bootstrap problem) and §13 (realms),
  [docs/reverse-proxy/README.md](../reverse-proxy/README.md) (the operator guide).

## Context

Watchtower had two half-concepts for the same thing — *a hostname that serves Watchtower itself*.

**One: implicit login-host synthesis.** `ProxySiteProjection.Project` added a `Local` site with **no
route row** for the configured `Auth:Host` and for every `Realm.AuthHost`, whenever `Auth:Enabled` was
on. It existed to answer the bootstrap problem in design.md §11: a protected app redirects an anonymous
visitor to `https://{loginHost}/login`, so that hostname has to be served before forward-auth is useful
for anything at all.

**Two: an explicit route row**, which an operator could create for the same hostname — and which the
projection then had to force-unprotect, because putting a login page behind the forward-auth that
redirects to that login page is a closed loop whose only way out is the published management port.

Both projected to the same `Local` site and both needed certificates, but only one of them was a row.
The consequences were spread across the codebase:

- `proxy.listCertificates` had a third source value, `loginHost`, for "a certificate with no route
  behind it" — and `ProxyRouteSnapshot.RouteId` was nullable purely to express the same thing.
- A login host had no status, no TLS toggle, no `DomainKind`, no DNS check and no audit trail, because
  those are all properties of a row.
- `YarpProxyProvider.ApplyAsync` and `CaddyManager.LoadSitesAsync` each had to remember to pass
  `RealmResolver.AuthHostsAsync()` into the projection; forgetting it silently un-served every realm's
  login page and re-gated any route on one of those domains.
- Every realm write had to reload the proxy, because a *column* decided which site blocks existed.

And the concept itself conflated two different facts. "This hostname serves Watchtower's UI" is an
**ingress** fact — which provider answers on which name. "Send anonymous visitors here" is an **auth**
fact, and it is still needed when somebody else's proxy serves Watchtower and no provider of ours
answers on that hostname at all.

## Decision

**A login host *is* a Watchtower self-route.** One table, one UX, one place per provider to optimise
the self path.

1. **`Route.Target ∈ {Service, Watchtower}`.** A `Watchtower` row has no stack; it carries
   `Route.RealmId` — the realm whose UI, portal and (when designated) login page the hostname serves.
   `Route.StackId` becomes nullable, and the check constraint `ck_routes_target` says which columns each
   kind may fill:

   ```sql
   (target = 'Watchtower' AND stack_id IS NULL AND realm_id IS NOT NULL AND access_mode = 'Public')
   OR (target = 'Service'    AND stack_id IS NOT NULL AND realm_id IS NULL)
   ```

2. **A Watchtower route is never behind route access control.** The `access_mode = 'Public'` clause
   above is the invariant "no realm's login host sits behind its own gate", made structural: the
   database will not store the closed loop. `proxy.setAccess` refuses such a route outright rather than
   accepting it as a no-op — an administrator who thought they had gated a hostname must find out that
   they have not. Nothing is lost: Watchtower authenticates its own surface natively (design.md §2.5).

3. **Each realm designates one of its Watchtower routes as its login route** —
   `Realm.LoginRouteId`, FK → `routes`, `ON DELETE SET NULL`. `RealmResolver.LoginHostForAsync(realm)`
   is that route's domain. **`Realm.AuthHost` is deleted.**

4. **The synthesis loop is deleted.** `ProxySite.Local` is derived purely from
   `Target == Watchtower`. Providers get sites; some are `Local`; each renders that its own way (below).
   Every served hostname is a row, so every one of them has a status, a TLS toggle, a certificate, a DNS
   check and an audit trail.

5. **`Auth:Host` is demoted to a fallback for the system realm only.** It answers
   `LoginHostForAsync(system)` when the system realm has no login route — the "Watchtower sits behind an
   external proxy and the operator prefers configuration to a row" case. A non-system realm in that
   position creates a Watchtower route anyway: unserved while our proxy is off, but still the one place
   its login address is written down.

6. **A one-time conversion**, in two parts because the two halves live in different places:
   - the **migration** `ConvertLoginHostsToRoutes` copies every realm's `auth_host` into a Watchtower
     route (TLS on, `Managed`, `Public`) and sets `login_route_id`, then drops the column. It has to be
     the migration: the column must be read before it is dropped;
     (regenerated away by [ADR-0024](0024-postgresql-only-and-state-in-the-database.md); the importer
     performs the conversion for legacy databases);
   - `Services/LoginHostConversion.cs`, run from `Program.InitializeDatabaseAsync` after migrations and
     before the providers start, does the `Auth:Host` half — which no migration can see, because it is
     configuration. Idempotent on the settings sentinel `Watchtower:Auth:LoginHostsConverted`, and it
     audits its work as `proxy` / `route.convert`. The migration half writes **no** audit rows: it is a
     schema migration, running before the application and its audit plumbing exist, and the migration
     history is the record of what it did.

   Neither half ever re-points a hostname that is already a **service** route: the operator has said
   what that hostname serves, and quietly moving it to the management plane would be the worst possible
   reading of an upgrade. The realm simply gets no login route, and the UI can designate another in one
   click. `LoginHostConversion` also stands down when the hostname already serves Watchtower for
   *another* realm, which the migration half can produce: taking a customer population's login page away
   is not an upgrade step's call to make.

### Per provider

| Provider | What a Watchtower route does |
|---|---|
| `yarp` (default) | `YarpHostDispatchMiddleware` sees `row.Local` and hands the request to Watchtower's own pipeline — SPA and all — on both the ingress and the management listener, with the ordinary HTTP→HTTPS upgrade. No forward, no hop. |
| `caddy` | The site block renders `reverse_proxy watchtower:8080` — the alias Caddy already reaches for forward-auth and the callback, on the control network. |
| `cloudflare` | Not supported: the route is set to `Error` with *"Watchtower routes are not served by the Cloudflare provider yet; expose Watchtower through Cloudflare's dashboard/Access. The hostname is still used as this realm's login address."* An ingress rule pointing at Watchtower would publish the management plane through the tunnel with nothing in front of it — which is exactly what Cloudflare Access exists to do properly. |

## Worked example

One instance, authentication on, the `yarp` provider, the management UI at `watchtower.example.com`,
and a customer realm `acme`:

| id | domain | target | stack / service:port | realm | login route? | access | served as |
|---|---|---|---|---|---|---|---|
| 1 | `watchtower.example.com` | Watchtower | — | system | ✔ (system) | Public (enforced) | in-process: management UI + operator login |
| 2 | `app.example.com` | Service | `myapp` / `web:3000` | system (via stack) | — | Authenticated | forwarded after forward-auth |
| 3 | `login.acme.com` | Watchtower | — | acme | ✔ (acme) | Public (enforced) | in-process: acme login page + "your applications" portal |
| 4 | `crm.acme.com` | Service | `acme-crm` / `web:8080` | acme (via template) | — | Restricted | forwarded after forward-auth |
| 5 | `admin.example.com` | Watchtower | — | system | — | Public | in-process: a second UI hostname, not used for redirects |

`realms`: system (login route → 1), acme (login route → 3). Settings: `Proxy:Enabled=true`,
`Provider=yarp`, `Auth:Enabled=true`, `Auth:Host` **empty**. Ports: `127.0.0.1:8080:8080` (management,
private), `80:8081`, `443:8443`.

What each request does:

- `https://watchtower.example.com/` → ingress → row 1 is `Local` → the in-process UI, `__wt_sso` login.
- `https://app.example.com/` anonymous → row 2 is protected → `AccessVerifier` → realm system → 302 to
  `https://watchtower.example.com/login?redirect_uri=…` → after login, `/.watchtower/callback` on
  `app.example.com` mints `__wt_access` → forwarded to `myapp-web:3000` with `X-Watchtower-Jwt`.
- `https://crm.acme.com/` anonymous → realm acme → 302 to `https://login.acme.com/login`.
- `http://watchtower.example.com/` on port 80 → 302 to https.
- `http://<public-ip>/` → 404: an unknown host on ingress is a stranger (ADR-0022).
- `http://nas.lan:8080/` → the management UI, on the port the operator bound privately.

All five rows get ACME certificates and show their status on the Routes page. Deleting row 1 is allowed
and reported: system-realm protected apps then fail closed (401, no login host) while the UI stays
reachable on 8080. Behind an external proxy — `Proxy:Enabled=false` — rows 1 and 3 still supply the
redirect hostnames; nothing is served or issued by us.

## Consequences

- **`Realm.AuthHost` is gone from the wire.** `realms.create` takes `loginDomain`, which creates the
  route and designates it; naming an *existing* route at creation is deliberately not offered, because a
  Watchtower route carries the realm it serves and none can belong to a realm that does not exist yet.
  `realms.update` takes `loginRouteId` (`0` clears it), which is where an existing route is designated.
  `RealmDto` reports `loginRouteId` and the effective `loginHost`.
- **The `Auth:Host` collision is refused on both sides.** `Auth:Host` is the operator realm's fallback,
  so pointing it at a hostname a customer realm serves Watchtower on would send operator visitors to a
  login page that cannot admit them and give both populations one token issuer.
  `system.updateAuthConfig` refuses such a host, and `proxy.createRoute` / `proxy.updateRoute` /
  `realms.create` refuse a non-system realm's Watchtower route on the configured `Auth:Host` — whichever
  of the two is written second is the one that is refused, so neither order reaches the collision.
- **`realms.delete` refuses a realm that still has Watchtower routes.** Those are public hostnames this
  instance answers on; removing them as a side effect of deleting a population is exactly the blast
  radius that handler already refuses to have. The foreign key is `RESTRICT` behind it.
- **Deleting a login route is allowed and reported.** `ON DELETE SET NULL`, and `proxy.deleteRoute`
  returns a warning naming the realm that now redirects nobody. Refusing would be worse: an operator
  retiring a hostname has said the hostname is going.
- **`proxy.listCertificates` loses its `loginHost` source.** `route` | `orphan` — every served host has
  a row now, and `orphan` means a certificate on disk that nothing routes to.
- **`/api/access/apps` excludes Watchtower routes.** The portal names applications a visitor can be sent
  to; the page the list is rendered on is not one of them.
- **The Cloudflare provider is now explicitly incomplete for this case**, and says so on the route
  rather than silently doing nothing.
- **Two rebuild-shaped migrations rather than one.** EF's SQLite generator hoists table rebuilds to the
  end of the migration they appear in while raw SQL keeps its position, so no ordering *within* one
  migration can make "`stack_id` is nullable" true at the moment the conversion inserts stack-less rows.
  `AddRouteTargetAndLoginRoutes` does the shape; `ConvertLoginHostsToRoutes` does the data.
  (Both regenerated away by [ADR-0024](0024-postgresql-only-and-state-in-the-database.md); the importer
  performs the conversion for legacy databases.)
- **Backward compatibility was not a goal**, beyond the one-time conversion: this is a pre-1.0 change to
  a seam only an operator's own configuration touches.

## Rejected alternatives

- **Keep `Realm.AuthHost` and merely stop synthesising sites.** Then a realm's login page would need a
  route row *and* a column agreeing with it — one more pair of facts that can disagree, and the
  disagreement would decide who is admitted where.
- **Make `Auth:Host` authoritative for the system realm forever.** It cannot carry a status, a
  certificate or an audit trail, and it made the operator realm a special case in every handler that
  touched a login host. It survives as a fallback for the one topology where a row genuinely has nothing
  to do.
- **A separate `watchtower_hosts` table.** Same rows, same uniqueness rule against `routes.domain`, same
  certificate machinery — and a second place to look when asking "what answers on this name". One table
  is the whole point.
