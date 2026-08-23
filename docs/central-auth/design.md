# Central Authorization — Access Control Plane for Proxied Webapps

> Status: Phase 1 implemented on branch `wt/watchtower-central-auth-84057b` (WI-1..WI-6). Phase 2 has begun: **groups + group-based grants are implemented** (`Groups` module, group subjects on `RouteAccessGrant`, group forwarding in the JWT and the per-mode ecosystem headers); OIDC upstream and template policy inheritance remain future work. **§4's MFA was designed and built on
2026-08-19**: TOTP with recovery codes, self-service in every realm, an administrative reset, and
break-glass clearing the second factor along with the password — per-realm *enforcement* is still future
work and belongs on the `Realm` entity (§13.8). **§13 (native multi-realm) was designed and built on 2026-08-10** — realms, per-realm credential spaces and login hosts, the realm access invariant and the operator-realm-only management surface all ship; **ADR-0023 (2026-08-23) revised how a login host is stored**, from `Realm.AuthHost` to a `Watchtower`-target route the realm designates. §12 (product-branded login pages/themes) is designed, not implemented.
> Branch/worktree: `watchtower-central-auth-84057b`.
> Grounded against the current code (Proxy module, `CaddyManager`/`CaddyConfigBuilder`, `Route`
> entity, host wiring in `Program.cs`) and Elarion `0.2.3-preview.79.1` (authorization API verified
> against the published docs).

## 1. Goal

Let an operator **centrally manage users and decide, per webapp, who may access it** — enforced at
the reverse proxy, the way Cloudflare Access works: an unauthenticated request to a protected app is
redirected to a central login page; after login the user lands back on the app with a session, and
the proxy forwards verified identity to the upstream. Apps that do their own auth are simply passed
through. By default identity lives in Watchtower itself (local users); for complex environments an
external OIDC provider (Keycloak, Entra, …) can be plugged in **as the identity source only** —
policy stays in Watchtower.

A second, deliberate consequence: **Watchtower's own UI becomes protected app #0.** The
`AnonymousCurrentUser` placeholder (`Watchtower.Api/AnonymousCurrentUser.cs` — "authentication is
the reverse proxy's job") is retired, and Elarion's dormant authorization machinery
(`[RequireRole]`, secure-by-default, the session capability model, frontend `when: { permission }`
gates) finally lights up — exactly as anticipated in
`docs/reverse-proxy/elarion-framework-notes.md` §C.

## 2. Design decisions (and why)

### 2.1 Watchtower is the *gate*; the identity provider is *pluggable behind it*

Caddy always forward-auths to **one** verify endpoint: Watchtower's. Watchtower owns the session
cookies, the login redirect flow, and — critically — the **per-app access policy**. The login page's
identity backend is what varies:

- **Local (default):** users in Watchtower's SQLite, password login.
- **OIDC upstream (v2):** the login page becomes "Continue with SSO" → standard OIDC code flow
  against Keycloak/Entra/anything; users are provisioned just-in-time and linked by `issuer + sub`.

We explicitly do **not** build a "Caddy talks to Keycloak directly" mode. That would move the
central promise — *which user may reach which app* — out of Watchtower into Keycloak
clients/roles, demoting Watchtower's UI to a read-only view of someone else's policy engine. With
the gate/IdP split, Keycloak answers "who is this person"; Watchtower always answers "may they
enter this app". Keycloak support shrinks from an alternative architecture to a login button.

### 2.2 Cross-domain sessions: the redirect dance is the *only* mechanism

Protected apps live on many hosts — managed subdomains *and* customer-owned custom domains
(`DomainKind.Custom`). A cookie set on the auth host is never sent to `app.customer.com`, so
"verify checks the central cookie" cannot work in general. We therefore implement the full
Cloudflare-Access-style dance (§5) and use it for **every** protected app, including same-parent
subdomains where a wildcard cookie could shortcut it. One code path, works everywhere, and it is
what the custom-domain story requires anyway. Two cookies exist:

- `__wt_sso` — the central SSO session, host-scoped to the auth host — since §13, to *its realm's* auth
  host, which is what makes the cookie jar and the population the same boundary. Established at login.
- `__wt_access` — a per-app session, host-scoped to the app's domain. Minted by the callback
  endpoint from a one-time code. Central logout revokes all of them (sessions are DB rows, §4).

### 2.3 Identity forwarding is a trust boundary: signed JWT + stripped convenience headers

Plain identity headers are trivially spoofable by clients unless the proxy strips inbound copies —
and trustworthy to the app only because of network topology. We do both of what Cloudflare does:

- **Convenience headers** on the authenticated request (refined in WI-8): plaintext identity headers
  are **opt-in per route** and use ecosystem-standard names — Authelia/Traefik `Remote-User`/
  `Remote-Name`/`Remote-Email` or oauth2-proxy `X-Auth-Request-User`/`-Preferred-Username`/`-Email`,
  selected by `Route.IdentityHeaderMode` — rather than a bespoke `X-Watchtower-*` name no
  off-the-shelf app recognises. The default is `None`: **JWT-only, no plaintext header.** Whatever the
  mode, they are copied from the verify response via `forward_auth { copy_headers … }`, and every
  protected site block first **strips the full identity/authz namespace of both ecosystems** from the
  incoming client request — a defense-in-depth **superset** of what we ever forward, including the
  `X-Forwarded-*` identity family we never set, so nothing reaching the upstream is client-forgeable.
  A mode also forwards **its own ecosystem's group header** — `Remote-Groups` or
  `X-Auth-Request-Groups` — carrying the account's group names sorted ordinal and comma-joined, and
  omitted entirely when the account is in no group (an empty header reads to some upstreams as
  membership of a group named `""`). Group names are constrained at creation to printable ASCII
  without commas precisely so this encoding is lossless. Both group names are in the strip set as well
  as the copy set, which is the invariant every forwarded name must satisfy.
- **A signed JWT** (`X-Watchtower-Jwt`, ES256) carrying `sub`, `preferred_username`, `email`,
  `groups`, `iss`, `aud` (the app's domain), `iat`/`exp`, verifiable against Watchtower's JWKS
  endpoint. `groups` is an array and is **always present** — empty when the account is in no group, so
  an app mapping groups onto roles can tell "no memberships" from "not answered" — and it is the
  trusted channel for group membership: unlike the plaintext header it does not depend on the proxy
  having stripped a forged copy first, and it is carried even by a `None` route. Apps that care
  validate cryptographically instead of trusting topology; apps with their own auth can consume it as
  an SSO assertion. The per-stack `watchtower-ingress-{stackId}` networks already guarantee the upstream is
  unreachable except through Caddy — that stays as defense in depth, not the load-bearing control.

### 2.4 Identity storage: ASP.NET Identity *core*, not the frame

Take `Microsoft.Extensions.Identity.Core` — `UserManager<User>`, `PasswordHasher`, lockout and
security-stamp machinery — over our own `User` entity with hand-written store interfaces
(`IUserStore`/`IUserPasswordStore`/`IUserLockoutStore`) backed by `WatchtowerDbContext`. We do
**not** pull in `IdentityDbContext` (the context is `[GenerateDbSets]`-generated and must not be
edited) and we do **not** use the scaffolded Identity UI (wrong shape for an Elarion-handler +
React-SPA app). Login/logout/callback/verify are plain HTTP endpoints (§6), matching the existing
convention that externally-facing/non-RPC surfaces live in `Watchtower.Api/Endpoints`; user CRUD is
a normal Elarion module.

### 2.5 Watchtower's own UI uses the same users — natively, not via forward-auth

Watchtower is typically reached on its published port, not through its own proxy, so its login is
**native**: ASP.NET cookie authentication + the same `/api/auth/login` endpoint, with Elarion's
claims-based `ICurrentUser` (`AddElarionCurrentUser`/`UseElarionCurrentUser`) replacing the
`AnonymousCurrentUser` singleton in `Program.cs:87`. Handler protection turns on secure-by-default:

- `[assembly: ElarionAuthorizationDefaults]` in `Watchtower.Application` — every handler requires
  authentication unless `[AllowAnonymous]`.
- `[RequireRole("Admin")]` on destructive/administrative handlers (user management, system config);
  a `Member` role for read/deploy scenarios can follow once someone needs it.
- The `elarion.session` bootstrap then reports real `isAuthenticated`/roles, and the SPA gains a
  login page plus role-gated navigation via the existing capability gating.

When `Auth:Enabled=false` (see §2.6) the anonymous singleton stays, exactly as today.

### 2.6 Opt-in rollout, explicit bootstrap

`Auth:Enabled` (default `false` for now — existing deployments must not lock themselves out on
upgrade). First-run bootstrap: if no **operator-realm** account exists — §13; an instance whose only
users live in a customer realm still has nobody who can administer it — create `admin` with the
password from `WATCHTOWER__AUTH__BOOTSTRAPPASSWORD`, else generate one and print it to the log — no
interactive step, no SMTP. Forward-auth for apps additionally requires `Proxy:Enabled` (the verify path is
meaningless without Caddy); Watchtower's own login works with the proxy off.

### 2.7 Scope discipline — what we are *not* building

- **No OIDC/SAML *provider* — for now, and the framing has changed (2026-08-10, see §13).**
  Originally this read "that is exactly the case we delegate to Keycloak". That delegation story is
  retired: Watchtower is the identity home and grows real IdP capabilities natively — the first of
  them, multiple realms, was built the same day (§13); external IdPs (Keycloak, Entra, …) are a
  **per-realm federation feature for customers who already run one** — the §2.1 login button — not
  Watchtower's growth path. What *survives* of this exclusion is the protocol scope: Watchtower does
  not expose an authorize endpoint / client registry / consent flow until a concrete integration
  needs one. Forward-auth + JWT covers the product fleet; the realm model that shipped does not
  preclude a protocol-level provider later.
- **No self-registration, invites, or email flows.** Admin creates users; admin resets passwords.
- ~~**No MFA in v1**~~ *(TOTP landed 2026-08-19 — see §4 and §7. Passkeys/WebAuthn remain out of scope,
  and MFA cannot yet be **required**: enforcement is per-realm policy and belongs on `Realm`, §13.8.)*
- **No service tokens in v1**, but the verify endpoint is designed for them (§5): per-route
  **bypass paths** cover webhooks/health endpoints now; `Authorization: Bearer <service-token>`
  slots into verify later without a flow change.

### 2.8 Staging

- **Phase 1 — the control plane:** users + sessions, native Watchtower login (app #0), per-route
  policy, forward-auth + redirect dance + JWT, bypass paths, audit events.
- **Phase 2 — identity federation & groups:** ~~groups + group-based grants~~ *(done)*; ~~TOTP~~ *(done)*;
  generic OIDC upstream (= Keycloak, as per-realm federation per §13), template policy inheritance for
  tenants.
- **Phase 3 (as needed):** service tokens, SCIM; branded login (§12) climbs its own ladder
  (tokens → per-category auth hosts → runtime themes) as products demand it. ~~Realms land when a
  second user population actually exists~~ — they landed first instead (§13, 2026-08-10): a second
  population is now a row rather than a migration, which is what the branded-login ladder's second
  rung turned out to need anyway.

## 3. Data model

Conventions mirror the existing entities (`Entities/*.cs` + `[EntityConfiguration]` classes,
`int Id` keys, snake_case tables, enums via `HasConversion<string>()`, DbSets from
`[GenerateDbSets]`), then one EF migration via the design-time factory.

```csharp
public sealed class Realm {                              // a user population (§13)
    public int Id { get; set; }                          // the seeded operator realm is id 1
    public required string Name { get; set; }            // display name; editable
    public required string Slug { get; set; }            // unique, immutable — the `realm` claim
    public int? LoginRouteId { get; set; }               // the Watchtower route its login page is
                                                         // served on = its cookie jar (ADR-0023);
                                                         // FK routes, ON DELETE SET NULL, unique
    public bool IsSystem { get; set; }                   // exactly one row: the operator realm
    public DateTimeOffset CreatedAt { get; set; }
}

public enum RouteTarget { Service, Watchtower }          // what serves the hostname (ADR-0023)

public sealed class User {
    public int Id { get; set; }
    public int RealmId { get; set; }                     // FK, Restrict; defaults to the operator realm
    public required string UserName { get; set; }        // unique per realm — (RealmId, normalized)
    public string? Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsAdmin { get; set; }                    // maps to the "Admin" role claim
    public bool Disabled { get; set; }
    // Identity-core lockout + stamp fields:
    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public required string SecurityStamp { get; set; }
    // v2 (OIDC link): public string? ExternalIssuer / ExternalSubject (unique pair)
    public DateTimeOffset CreatedAt { get; set; }
}

public enum SessionKind { Sso, App }                     // central login vs per-app-domain session

public sealed class AuthSession {                        // revocable server-side sessions
    public int Id { get; set; }
    public required string TokenHash { get; set; }       // hash of the random cookie value
    public int UserId { get; set; }
    public User? User { get; set; }
    public SessionKind Kind { get; set; }
    public int? RouteId { get; set; }                    // App sessions: which app; SSO: null
    public DateTimeOffset ExpiresAt { get; set; }        // sliding; absolute cap via CreatedAt
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LoginCode {                          // the one-time cross-domain handoff code
    public int Id { get; set; }
    public required string CodeHash { get; set; }
    public int UserId { get; set; }
    public int RouteId { get; set; }
    public required string RedirectUri { get; set; }     // validated: https + host == route domain
    public DateTimeOffset ExpiresAt { get; set; }        // ~60 s, single use (deleted on redeem)
}

public enum AccessMode { Public, Authenticated, Restricted }

// Route (existing entity) gains:
public AccessMode AccessMode { get; set; } = AccessMode.Public;   // Public = today's behavior
public string? BypassPaths { get; set; }                           // newline-separated prefixes
public RouteTarget Target { get; set; } = RouteTarget.Service;     // ADR-0023
public int? StackId { get; set; }                                  // null iff Target == Watchtower
public int? RealmId { get; set; }                                  // set iff Target == Watchtower
// A *service* route has no RealmId: its realm is its stack's category's (below). A Watchtower route
// has no stack to inherit from and states its realm outright — and is always Public, which the
// check constraint ck_routes_target enforces along with the two rules above.

// StackTemplate (existing entity) gains:
public int RealmId { get; set; }                                   // a category lives in exactly one
                                                                   // realm; its routes inherit it

public sealed class Group {                              // a named set of accounts
    public int Id { get; set; }
    public int RealmId { get; set; }                     // FK, Restrict; groups never cross realms
    public required string Name { get; set; }            // printable ASCII, no comma (see below)
    public required string NormalizedName { get; set; }  // unique per realm (the User precedent)
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GroupMember {                        // unique (GroupId, UserId); both FKs cascade
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int UserId { get; set; }
}

public sealed class RouteAccessGrant {                   // subjects allowed when Restricted
    public int Id { get; set; }
    public int RouteId { get; set; }
    public int? UserId { get; set; }                     // exactly one of the two is set — enforced by
    public int? GroupId { get; set; }                    // CHECK ck_route_access_grants_subject
}

public sealed class AuditEvent {                         // THE audit trail (shared with every plane)
    public int Id { get; set; }
    public required string Category { get; set; }        // auth / access / users / groups / realms …
    public required string Action { get; set; }          // login.ok / login.failed / access.denied / …
    public required string Target { get; set; }          // the account, app, group or realm — by name
    public string? Actor { get; set; }                   // the acting account's name; null = system
    public int? RouteId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

Policy attaches to **`Route`** — the domain is the "app" as users experience it. `Public` means "no
forward_auth, just proxy" (apps with their own login). Phase-2 templates get
`StackTemplate.AccessMode` (+ template-level grants) copied to the auto-created route at add-tenant
time, same as env vars are merged today.

A grant names **one** subject, a user or a group, which is why uniqueness is two *partial* unique
indexes — `(RouteId, UserId) WHERE user_id IS NOT NULL` and `(RouteId, GroupId) WHERE group_id IS NOT
NULL` — rather than one composite index: the pair is unique within a subject kind, and a route may
name both a user and a group that user belongs to (that is simply access twice over, not a conflict).
Membership is resolved **per request** inside the grant query (`RouteAccessPolicy`), so a member holds
no grant row of their own and revoking membership or deleting the group takes effect on the next
request, with no cache to invalidate. Group names travel to upstreams (§2.3), so the charset rule is
enforced where names enter the system rather than escaped at each forwarding site.

The three realm foreign keys are `Restrict`, never `Cascade`: deleting a population must not be the
operation that discovers its own blast radius, so `realms.delete` refuses while anything still
points at the row (§13). Uniqueness moved with them — `(realm_id, normalized_user_name)` and
`(realm_id, normalized_name)`, so two populations may each have an `admin` and neither can see the
other's. Two names stayed **global** on purpose: `routes.domain`, because a domain is a global
resource — DNS and Caddy's site blocks have no notion of realms, so two realms claiming one host
could not both be served — and `stack_templates.name`, because a template name is what an operator
picks a category by in the one management surface there is, and that surface is operator-realm-only,
so one flat namespace is exactly what they see.

Signing material: one ES256 key pair, generated on first use, stored under `/data` next to the
SQLite db (`Auth:KeyPath`, default `/data/auth-keys/`). ASP.NET Data Protection keys (cookie
encryption) are persisted to the same directory — otherwise every restart logs everyone out.

## 4. Sessions & tokens

- Cookie values are 256-bit random tokens; the DB stores only their hash. Cookies are `HttpOnly`,
  `Secure`, `SameSite=Lax`, host-scoped (no `Domain` attribute).
- The **SSO session** (`__wt_sso`, auth host) is created at login; default lifetime 12 h sliding,
  7 d absolute (both under `Auth:` options).
- **App sessions** (`__wt_access`, app host) are minted only via the callback and reference their
  `RouteId`; verify consults the DB (SQLite point-read per request is fine at Watchtower's scale;
  an in-memory cache keyed by token hash with short TTL + invalidation on revoke is a follow-up).
- **Logout:** the auth host's logout deletes the SSO session and all App sessions of that user
  (global sign-out); a per-app logout path (`/.watchtower/logout` on the app domain) clears just
  that app's cookie/session.
- A **pending-MFA record** (`SessionKind.MfaPending`, 5 min) is the half-finished login of a two-factor
  account: same table, same hashing, same expiry sweep, but returned in the response *body* and never as
  a cookie. It authenticates nothing — every lookup that turns a token into a principal filters to
  `Sso`/`App` by an allow-list, so adding a future kind forces a decision rather than inheriting one.
- **Two-factor (TOTP)** is ASP.NET Identity's own RFC 6238 authenticator provider over the custom user
  store (no TOTP package). Recovery codes are SHA-256 hashes in `user_recovery_codes`, deleted on
  redemption — the affected-row count is what makes a code single-use under concurrency.
- The **JWT** is minted per verified request (cheap: one ES256 sign) with `aud` = app domain and
  short `exp` (5 min) — it is an assertion about *this request*, not a bearer credential.

## 5. Request flows

### Verify (every request to a protected app)

Caddy `forward_auth` sends the original headers (incl. `Cookie`) plus `X-Forwarded-Method` /
`X-Forwarded-Uri`, with `X-Forwarded-Host` identifying the app. `GET /api/access/verify`:

1. Resolve the `Route` by `X-Forwarded-Host`; `AccessMode == Public` should never reach here (no
   `forward_auth` is emitted for it) — treat as 200 defensively.
2. If `X-Forwarded-Uri` matches a bypass prefix (or the reserved `/.watchtower/*` paths) → 200.
3. (Later: `Authorization: Bearer` service token → 200 with service identity.)
4. Validate the `__wt_access` cookie → session row for this `RouteId`, not expired, user not
   disabled; for `Restricted`, user must hold a grant. On success → **200** with the response header
   `X-Watchtower-Jwt` always set, plus — only when the route opted into a header mode — that mode's
   plaintext identity names (`Remote-*` or `X-Auth-Request-*`); all copied onto the proxied request
   by `copy_headers`.
5. On failure: browser navigation requests (`GET`/`HEAD` with `Accept: text/html`) → **302** to
   `https://{authHost}/login?redirect_uri={original URL}`, where `authHost` is **the route's realm's**
   login host (§13) — a visitor is only ever sent to the login page of the population that could
   admit them, and a realm with no host yet fails closed with a bare 401 instead. (Caddy forwards the
   auth response to the client on non-2xx, so the redirect just works.) Everything else (XHR, POST) →
   plain **401** — never redirect a POST into a login page.
6. Authenticated but not authorized (`Restricted`, no grant) → **403** with a small "access denied"
   page and an `access.denied` audit event.

### Login dance (first visit to `app.customer.com`)

1. Verify fails → 302 to `https://auth.example.com/login?redirect_uri=…` (the SPA login page).
2. No `__wt_sso`? User authenticates (local password v1; OIDC upstream v2 does the code flow here
   and returns with identity). SSO session + cookie are created.
3. The login endpoint validates `redirect_uri` (https, host must match an existing route's domain —
   an open-redirect guard), checks the user is *authorized for that app* (fail fast with the 403
   page rather than a redirect loop), then mints a `LoginCode` and responds
   302 → `https://app.customer.com/.watchtower/callback?code=…`.
4. `/.watchtower/*` on every protected site is routed by Caddy **to Watchtower, not the upstream**
   (§6). The callback redeems the code (single use, 60 s, host-bound), creates the App session, sets
   `__wt_access`, and 302s to the originally requested URI.
5. Subsequent requests hit verify → 200 with identity headers. Apps on other domains repeat only
   steps 3–4 (SSO cookie already present): silent SSO.

## 6. Proxy config changes

Two providers run this gate in front of an app: the deprecated **Caddy** container, whose generated
configuration is below, and the **in-process** provider (ADR-0022), which renders exactly the same
contract as pipeline steps. Both call the same `AccessVerifier`, so they cannot come to different
verdicts. The **Cloudflare Tunnel** provider is not in this picture at all — there access belongs to
Zero Trust.

Note what is *not* here: a site block for Watchtower's own hostnames. Since ADR-0023 those are
ordinary route rows with `Target == Watchtower`, projected as `ProxySite.Local` and rendered per
provider — Caddy writes `reverse_proxy watchtower:8080`, the in-process provider hands the request to
its own pipeline, Cloudflare marks the route `Error`. They are never `Protected`, and the database
will not store one that is.

### 6.1 Caddy provider

`ProxySite` gains `bool Protected` (+ bypass data as needed); `CaddyConfigBuilder.Build` emits for
protected sites (unchanged for `Public` ones):

```
app.customer.com {
	# Auth plumbing served by Watchtower itself on the app's own domain (callback/logout).
	handle /.watchtower/* {
		reverse_proxy watchtower:8080
	}
	handle {
		# Clients must not be able to smuggle identity headers: strip the full ecosystem
		# identity/authz namespace (a superset of anything we forward), so even headers we
		# never set — e.g. the group headers — cannot be forged.
		request_header -X-Watchtower-Jwt
		request_header -Remote-User
		request_header -Remote-Groups
		request_header -X-Auth-Request-User
		request_header -X-Auth-Request-Groups
		# … full ecosystem identity/authz namespace (Remote-*, X-Auth-Request-*, X-Forwarded-* identity)
		forward_auth watchtower:8080 {
			uri /api/access/verify
			# JWT always; the mode's plaintext names only for a header-mode route
			# (here: Remote — omit for the default None / JWT-only route).
			copy_headers X-Watchtower-Jwt Remote-User Remote-Name Remote-Email
		}
		reverse_proxy myapp-web:3000
	}
}
```

`watchtower:8080` is the existing `SelfAlias` on `watchtower-control` — the same path the
on-demand-TLS `ask` endpoint already uses (`CaddyManager.ApplyAsync`), so **no new network plumbing
is required**: Caddy can already reach Watchtower off the public path. `forward_auth` is Caddy's
purpose-built shorthand for this exact pattern; SSE/WebSocket upgrades pass through it unaffected
(only the initial request is checked).

One new requirement: every **login host** must itself be reachable through Caddy — i.e. Watchtower
needs a route to itself, one per realm that has a login page. Since ADR-0023 that is literally a route
row (`Target == Watchtower`), and Caddy renders it as an ordinary site block with upstream
`watchtower:8080`. This also finally gives the Watchtower UI TLS through its own proxy; the management
port stays as the escape hatch (and is how you recover from a proxy misconfiguration). None of these
site blocks is ever protected — see §13, the one invariant the model enforces.

### 6.2 In-process (`yarp`) provider

The same site block, expressed as middleware instead of generated configuration. `YarpHostDispatchMiddleware`
runs *before* Watchtower's own routing and, for a `Host` in the route table:

1. **Strips** the full ecosystem identity/authz namespace from the inbound request — the same
   superset the `request_header -…` lines remove, plus `X-Forwarded-Method` and `X-Forwarded-Uri`,
   which describe a forward-auth hop that does not exist here and are therefore just strings a client
   wrote. Stripped on *every* route rather than only inside the forwarded branch, so nothing smuggled
   reaches Watchtower's own endpoints either.
2. **Serves `/.watchtower/*` locally**, with `X-Forwarded-Host`/`-Proto`/`-For` stamped — the
   equivalent of the `handle /.watchtower/*` block, and for the same reason: the callback binds the
   authorization code to the domain it is redeemed on by reading `X-Forwarded-Host`, and it has to
   answer while the visitor is still anonymous.
3. **Calls `AccessVerifier` directly** where `forward_auth` would have made an HTTP hop. Same
   decision core, same verdicts; the real method and URI are used instead of the forwarded ones.
4. **Sets the identity headers on the outgoing request** instead of having them lifted off a
   forward-auth response by `copy_headers`, then forwards to `{project}-{service}:{port}` with YARP's
   `IHttpForwarder`.

A Watchtower route (ADR-0023 — a realm's login host among them) is dispatched to Watchtower's own
pipeline rather than forwarded, because forwarding it would be forwarding to ourselves. It still gets
the HTTPS upgrade every TLS route gets, for the same reason it always did: a login page reached over
plain HTTP would set the session cookie without `Secure`.

`AccessVerifier` is the single decision core both paths share. It was extracted from the
`/api/access/verify` endpoint precisely so that adding a second transport could not fork the rules.

## 7. Backend surface

**HTTP endpoints** (in `Watchtower.Api/Endpoints`, next to the webhook/SSE endpoints, per the
existing convention for non-RPC/external surfaces):

| Endpoint | Purpose |
|---|---|
| `POST /api/auth/login` | Password login (SPA form); sets `__wt_sso` (+ native Watchtower session). Rate-limited + Identity lockout. Accepts optional `redirect_uri` to continue the dance. For a two-factor account it sets **no cookie** and answers `{ mfaRequired, mfaToken }` instead. |
| `POST /api/auth/login/mfa` | Redeems that challenge with a TOTP or a recovery code and finishes the login — same cookie, body and `continueUrl` semantics as above. Same rate limiter; a wrong code counts against the lockout. Bound to the realm of the host it arrives on. |
| `POST /api/auth/logout` | Global sign-out (revokes SSO + all app sessions). |
| `GET /api/auth/mfa` | The caller's own two-factor state (`totpEnabled`, `recoveryCodesRemaining`). |
| `POST /api/auth/mfa/totp/begin` | Mints an authenticator key; returns it as a shared key + `otpauth://` URI. Refused (409) while two-factor is already on. |
| `POST /api/auth/mfa/totp/confirm` | Turns two-factor on given a valid code **and** the account password; returns the ten recovery codes, once. |
| `POST /api/auth/mfa/totp/disable` | Turns it off given a TOTP *or* a recovery code; clears the key and every code. |
| `POST /api/auth/mfa/recovery/regenerate` | Replaces the recovery codes given a TOTP code (a recovery code is refused). |
| `GET /api/access/verify` | The `forward_auth` target (§5). Internal (control network). |
| `GET /api/access/userinfo` | OIDC UserInfo (Core §5.3): identity claims for a Bearer JWT or the app-session cookie. |
| `GET /.watchtower/callback` | Code redemption on the app domain (§5). |
| `GET /.watchtower/logout` | Per-app sign-out. |
| `GET /api/auth/jwks` | Public JWKS for `X-Watchtower-Jwt` verification. |

**Modules** (normal Elarion handlers, JSON-RPC):

- `Users` module: `users.list/create/update/delete/resetPassword/setDisabled/resetMfa` — all
  `[RequireRole("Admin")]`. `resetMfa` is one-directional: it removes a second factor and there is no
  handler that adds one, because enrolling requires a code only the account's owner can produce.
- `Realms` module: `realms.list/create/update/delete` — all `[RequireRole("Admin")]`, and
  operator-realm-only twice over (§13: the role is emitted for system-realm accounts only, and every
  handler additionally passes the system-realm gate).
- `Groups` module: `groups.list/create/rename/delete/getMembers/setMembers` — all
  `[RequireRole("Admin")]`, because putting an account in a group grants it every route that group is
  named on. `setMembers` is a whole-set replace, reconciled like `proxy.setAccess`.
- `Access` handlers (fold into the existing `Proxy` module — policy is route-scoped):
  `proxy.getAccess` / `proxy.setAccess` (mode + grants + bypass paths), calling
  `CaddyManager.ApplyAsync()` on change like the route CRUD does.
- `Audit` module: `audit.listEvents` / `audit.listFacets` — both `[RequireRole("Admin")]`, and both
  **read-only**. The access-control plane writes into the instance's one audit trail (`AuditEvent`,
  categories `auth` / `access` / `users` / `groups` / `realms`; the kinds in `AuthEventKinds` are the
  actions) alongside everything Watchtower itself does (`proxy.cloudflare`, `backups`, `system`, …), so
  one view answers both "who did what" and "what did Watchtower do as a result". The writers stay in the
  modules whose acts they record (and in the login endpoints), adding the row to their own transaction
  via `AuthAudit.QueueAsync` so it commits with the act; a disabled `Audit` module hides the view
  without interrupting the recording. `audit.listEvents` is keyset-paged on the primary key
  (`Id < beforeId`, `ORDER BY Id DESC`, default 100 rows, clamped at 500) rather than offset-paged,
  because the table is append-only and is being written while it is read; ordering by `Id` rather than
  `CreatedAt` is also forced by SQLite, which cannot `ORDER BY` a `DateTimeOffset` (the same limitation
  `stacks.events` works around). `audit.listFacets` reports the distinct categories, actions and actors
  actually present, so the filters never drift from the vocabulary. Rows are reference-free: actor and
  target are recorded by name, so a row about a deleted account keeps everything it said.
- Secure-by-default via `[assembly: ElarionAuthorizationDefaults]`; `[AllowAnonymous]` on the
  session-bootstrap surface and anything the login page needs pre-auth.

**Host wiring** (`Program.cs`): standard cookie authentication for the native UI session +
`UseElarionCurrentUser` snapshotting the principal (replacing the `AnonymousCurrentUser`
registration at `Program.cs:87` when `Auth:Enabled`); ASP.NET rate limiter on the login endpoint;
Data Protection persisted to `/data`.

**Options:** `AuthOptions { Enabled, Host, SessionLifetime, AbsoluteSessionLifetime, KeyPath }`
alongside `ProxyOptions` in `Config/WatchtowerOptions.cs`; bootstrap password via env var (§2.6).

## 8. Frontend

- **Login page** (pre-auth route in the SPA) posting to `/api/auth/login`; the session bootstrap's
  `isAuthenticated` drives the redirect-to-login gate.
- **Account security page** (`/account/security`, platform-owned like the login page and gated by
  nothing): TOTP status, enrolment with a QR code and manual-key fallback, recovery-code display, and the
  disable/regenerate flows. Deliberately *not* a feature module — every account may protect its own
  credentials, including one outside the operator realm, for which the shell renders the applications
  portal instead of module routes and therefore lets this one path through explicitly.
- **Users page** (new `users` frontend module, gated `when: { module: 'Users' }` + admin role), showing
  each account's two-factor state and offering the administrative reset.
- **Groups page** (new `groups` frontend module, gated the same way): group CRUD plus a members
  roster over the account list.
- **Route form** gains an *Access* section: mode selector (`Public` / `Any authenticated user` /
  `Selected users and groups`), user picker, group picker, bypass-path list.
- **Audit page** (the `audit` frontend module, gated the same way): the trail newest-first, filtered by
  category, action and actor, with a *Load more* button following `nextBeforeId` and a manual refresh.
  Read-only — the screen issues no mutations, and there is deliberately no live tail: the filters are part
  of the query key, so changing one starts a fresh first page rather than appending an unrelated result to
  the rows on screen.
- Schema regeneration as usual — pass an **absolute repo-root path** for the output:
  `dotnet run --project src/Watchtower.Api -- --export-schema "$PWD/rpc-schema.json"`. A bare relative
  path lands in `src/Watchtower.Api/` because `dotnet run --project` runs the app with its CWD set to the
  project directory (via launchSettings), not the repo root; CI enforces freshness against the repo-root
  file.

## 9. Security hardening checklist (v1-blocking)

- Login: per-IP + per-user rate limiting, Identity lockout (5 failures / 15 min), constant-time
  comparisons, generic failure message.
- Cookies: `HttpOnly` + `Secure` + `SameSite=Lax`, host-scoped, hashed server-side.
- `redirect_uri` allow-listing against the route table (open-redirect guard) at *both* login and
  code-mint time; codes single-use, short-TTL, host-bound.
- Strip inbound `X-Watchtower-*` in every protected site block; JWT `aud` binds identity to the app.
- CSRF: login/logout are same-origin SPA POSTs (`SameSite=Lax` + custom-header check); callback is
  a GET but redeems a single-use code, so replay/forgery yields nothing.
- Audit `AuthEvent`s for login success/failure, denials, user + policy changes.
- Keys under `/data` (survive restarts); document that rotating the JWT key invalidates in-flight
  assertions only (5-min lifetime).

## 10. Milestones

**Phase 1**
1. Entities + stores + migration (`User`, `AuthSession`, `LoginCode`, `Route.AccessMode`/
   `BypassPaths`, `RouteAccessGrant`, `AuthEvent`); Identity-core wiring + bootstrap admin.
2. Native Watchtower login: cookie auth, `/api/auth/login|logout`, claims `ICurrentUser`,
   `[assembly: ElarionAuthorizationDefaults]` + `[AllowAnonymous]` pass, SPA login page. *(Watchtower
   is now protected app #0 — shippable on its own.)*
3. `Users` module + UI.
4. Forward-auth: verify endpoint, callback/logout on app domains, `CaddyConfigBuilder` protected
   site blocks + auth-host self-route, JWT + JWKS.
5. Route access policy: entity-backed `proxy.getAccess`/`setAccess`, route-form Access section,
   bypass paths.
6. Hardening + audit (§9), docs (`docs/central-auth/README.md` user guide, deploy-manifest notes).
7. End-to-end verification: protected app on a custom domain, full dance, silent SSO on a second
   app, denial page, bypass path, header-smuggling attempt rejected.

**Phase 2** — ~~groups + group grants~~ *(done)*; generic OIDC upstream (JIT provisioning,
`issuer+sub` linking), template access inheritance.

## 11. Risks / open questions

- **Login-host bootstrap:** the login page must be reachable *before* auth is useful — a protected app
  redirects there, so the hostname has to be served and hold a certificate first. Since ADR-0023 that
  is one concrete step rather than a configuration/synthesis pair: **create a Watchtower route and mark
  it as the realm's login host**, point its DNS at the instance, and watch the route go `Active` on the
  Routes page. Each realm needs its own. A realm without one is a legitimate state that fails closed —
  a bare 401 rather than a redirect somewhere arbitrary — and only the operator realm has a fallback
  (`Auth:Host`, for the case where somebody else's proxy serves the hostname).
- **Locked-out operator:** the old shape of this risk was "wrong policy on the Watchtower self-route".
  It is now structural: `ck_routes_target` refuses to store a Watchtower route that is not `Public`,
  and `proxy.setAccess` refuses one outright. What remains is the ordinary one — deleting the login
  route, which is allowed and warned about. The management port + native login always works; document
  it as the recovery path. A `WATCHTOWER__AUTH__RESETPASSWORD` env hook (applied at startup) is the
  break-glass.
- **`/.watchtower/*` path collision** with an app that genuinely uses that prefix: reserved-prefix
  approach is what Cloudflare (`/cdn-cgi/`) does; document it, make the prefix constant.
- **Verify latency:** one SQLite read + one ES256 sign per request through the proxy. Fine at this
  scale; cache is a known follow-up (§4), not a v1 requirement.
- **Non-browser clients** of protected apps (API tokens, mobile): bypass paths are the v1 answer;
  service tokens are designed-for but deferred — confirm this is acceptable for the first real
  deployments.
- **Elarion secure-by-default sweep:** flipping `[ElarionAuthorizationDefaults]` on requires
  auditing every existing handler + the SSE/webhook endpoints for the right `[AllowAnonymous]` /
  cookie treatment (the deploy webhook keeps its own bearer scheme; container-log SSE must now
  require the native session). This is the widest-blast-radius step — do it in its own PR (milestone
  2) with the app #0 rollout.

## 12. Product-branded login pages & themes *(designed 2026-08-10, not implemented)*

### 12.1 Goal

A user hitting a protected product app should experience the **product's** login, not Watchtower's:
product name, logo, colors — up to a fully product-designed page — on a login domain that reads as
the product's (`login.product-a.com`). Watchtower never surfaces. The unit of branding is the
**product category = `StackTemplate`** (every tenant of a category shares its login), with a
route-level override for custom routes.

Precedents that anchor the design: **Keycloak/Keycloakify** (component-level themes, centrally
executed flow, themes are trusted deploy-time code) and **Auth0 hosted login** (full custom pages
applied at *runtime*, gated purely on dashboard access). We take Keycloakify's *contract* — theme
owns the chrome, IdP owns the flow — and Auth0's *trust model* — runtime application gated on
operator access. The trust argument: a Watchtower principal can already deploy arbitrary containers
and rewrite the Caddy config; uploading login markup adds no privilege they don't have.

### 12.2 Invariants (the security ground rules everything below obeys)

1. **Server-resolved branding, keyed by `Host` only.** The login page already refuses to render
   anything derived from `redirect_uri` (`LoginPage.tsx`, `lib/auth.ts` — it is attacker-reachable
   and is passed to the backend, never echoed). Branding keeps that invariant: the SPA asks the
   backend "what branding applies here?", the backend answers from server-owned config keyed by the
   validated request `Host`, and an unknown host fails closed to the built-in Watchtower page.
   Host-keyed lookup also avoids the enumeration leak a `redirect_uri`-keyed lookup would create
   (probing `/login?redirect_uri=…` to learn which domains are Watchtower-protected).
2. **No requester-controlled theme selection.** Nothing the requester controls — query params,
   `User-Agent`, `Accept-Language` — may influence which theme renders on a given host. DNS + TLS
   pin the host; the host pins the theme. The moment an attacker can steer which skin renders,
   the IdP contains a phishing kit.
3. **Watchtower owns the flow; the theme owns the chrome.** All auth logic — form submission, CSRF,
   error/handover states, `redirect_uri` handling — lives in a Watchtower-injected **auth kernel**
   (a `<watchtower-login>` element + script), single-sourced and updatable by us after any number of
   themes exist. Themes place and style it; they never reimplement it. The kernel API is versioned
   (`kernelApiVersion`, §12.4) so incompatible themes fail **at apply time** with a clear error, not
   at login time in a user's browser.
4. **Custom themes require a per-category auth host.** Blast radius of a bad theme = one category:
   its own origin, its own cookie jar. The shared default `Auth:Host` only ever renders the
   built-in page.
5. **Strict CSP on themed responses** (`default-src 'self'; form-action 'self'; connect-src
   'self'`): bundles must be genuinely self-contained — no external scripts, fonts, or beacons.
   Cheap (Watchtower controls Caddy/response headers) and it structurally closes "theme JS
   exfiltrates credentials somewhere".
6. **Fail closed to the built-in page, never to an error.** Missing digest on disk, corrupt tree,
   incompatible kernel version — the user always gets a working (default) login.

Accepted consciously: per-product login looks weaken the "login always looks the same"
anti-phishing property — the URL becomes the trust anchor. The product-recognizable custom domain
mitigates this; passkeys/WebAuthn is the structural fix if it ever matters.

*Credential visibility dial (decided: full trust).* With the form in the theme's DOM, theme JS
could read credentials — acceptable under the operator-trust argument, same as Auth0 classic. The
back-pocket upgrade if theming ever opens to less-trusted parties: the theme becomes pure chrome
around a form iframe served from the central `Auth:Host` origin (cross-origin → theme JS cannot
reach the fields), at the cost of styling flexibility inside the form.

### 12.3 The ladder

1. **Branding tokens** on `StackTemplate`: product name, logo, brand colors. The SPA theme system
   is already CSS variables (`--brand-*` in `styles.css`), so tokens are data, not code. Covers
   most products.
2. **Per-category auth hosts** — *the mechanism landed on 2026-08-10, realm-shaped rather than
   template-shaped.* Every login host is served (as a Watchtower route since ADR-0023), and
   the `/api/access/verify` 302 does pick the login host per route rather than from the global
   `Auth:Host` — but the host hangs on the realm's login route, not on `StackTemplate.AuthHost`, and the
   302 follows the route's **realm** (§13). So what a category gets today is a *realm*
   (`StackTemplate.RealmId`), and its tenants log in on that population's host. That is the stronger
   of the two shapes for the same effort: a host that is also a cookie jar is a population boundary
   whether or not anyone meant it to be, so making it one explicitly beats having it be one by
   accident. What is still unbuilt is a vanity host *per category within one realm*, with the silent
   hop to the realm host — see §13.8.
3. **Runtime theme bundles** (§12.4–12.6): full product-designed pages.

### 12.4 Theme package: an OCI image as delivery vehicle — pulled and extracted, never run

A theme is static files; a *running* theme container is rejected explicitly: it puts a process on
the login critical path (theme container down → login 502s → every outage recovery blocked), it
takes Watchtower out of the response path (no kernel injection, no CSP, the container could serve
its own form — the §12.2.3 contract becomes unenforceable), and it's a resident process per
category for static assets on a single-node estate. **The registry is the transport; Watchtower is
the host.** Products build/version/push theme images exactly like their app images — CI keeps the
login LAF in sync with the product release train.

**Image layout contract (v1):**

- Single-layer image (`FROM scratch` recommended) — sidesteps OCI layer-flattening/whiteout
  handling entirely; Watchtower untars exactly one layer.
- All content under `/theme/`; anything else is ignored.
- `/theme/index.html` — required entry point; must contain the kernel mount point
  (`<watchtower-login>`); validated at apply time, rejected if absent.
- `/theme/theme.json` — manifest: theme name, version, **`kernelApiVersion`** (the compatibility
  gate for kernel evolution).
- OCI label `dev.watchtower.login-theme.api=1` — lets Watchtower reject "not a theme image" from
  the manifest alone, before pulling layers.

**Apply semantics:** reference by tag, resolve and **store by digest** (tag is UX, digest is truth
— integrity, audit, rollback). Extraction treats the archive as hostile even though the uploader is
trusted (registries and CI are in the supply chain): path-traversal/zip-slip checks, symlinks and
hardlinks rejected outright, regular files only, size and file-count caps. Extracted trees are
stored content-addressed by digest under `/data`; applying = an atomic pointer flip; previous
digests retained for rollback; every apply/revert writes an `AuthEvent`. A plain HTTP zip upload
(per ADR-0003, file transfer stays plain HTTP rather than JSON-RPC) is the second front door into
the identical storage path, for manual/small-shop use.

### 12.5 Theme library & assignment

Registration and assignment are separate operations:

```csharp
public sealed class LoginTheme {
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string SourceImageRef { get; set; }   // tag as given by the operator
    public required string ActiveDigest { get; set; }     // truth; history retained for rollback
    public required string KernelApiVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// StackTemplate gains:  int? LoginThemeId   (null = built-in default)  + AuthHost + branding tokens
// Route gains:          int? LoginThemeId   (override; see constraint below)
```

- `auth.addTheme(imageRef)` pulls, validates, extracts, stores — affects no login page yet.
  Assignment is a reference: the template/route form's dropdown is exactly the nullable FK
  (*Default (Watchtower)* + every registered theme), added with the same additive-optional-param
  convention as `IdentityHeaderMode`.
- **Resolution chain: route override → template → realm default (§13) → built-in.**
- **Route-level overrides only take effect on routes whose category has its own auth host.** On the
  shared `Auth:Host`, per-route themes would require a redirect-target-keyed lookup — reopening the
  enumeration leak §12.2.1 closes.
- Re-registering a theme (new digest) **follows head**: every assignment picks it up — one shared
  theme across categories updates in one place; digest history covers rollback. (Pin-by-digest per
  assignment was considered and rejected: ceremony without benefit while theme authors are the same
  trusted operators.)
- Because themes exist independent of assignment, two things come almost free: **preview** (render
  any registered theme at an admin-session preview URL with fake flow context — dummy errors,
  sample handover — before it goes live) and **staged rollout** (assign the new version to one
  low-stakes category first).

### 12.6 Serving & management-API integration

Watchtower serves the extracted tree on the category's auth host; `/.watchtower/*` and the API
paths always win over theme paths; the kernel script is injected and the CSP stamped on the way
out; any inconsistency falls closed to the built-in page (§12.2.6).

Theme registration/assignment rides the public management API: a product's management stack (the
central-management scenario) applies its login theme for its category at provisioning time,
authenticated as its Watchtower principal — the product ships its login LAF the same way it ships
everything else, no human upload step.

### 12.7 Flow-state pages

Error page, expired-link page, logout confirmation are **states the kernel hands the same theme**,
not separately assignable theme types (Keycloak's login/account/email theme split is a management
burden we deliberately don't reproduce). The server-rendered minimal HTML pages
(`WatchtowerAccessEndpoints.Html`) remain the themeless fallback.

## 13. Native multi-realm *(decided and built 2026-08-10)*

**Watchtower supports multiple realms natively.** External IdPs (Keycloak, Entra) are a **per-realm
federation feature** for customers who already manage an IdP — the §2.1 login button — *not*
Watchtower's exit ramp for growing identity needs. Watchtower is the fully-integrated identity home.
(This inverts the original §2.7 delegation framing; §2.7 has been updated.)

This section was written as a direction with seams to keep open, and was built the same day. What
follows is what exists; §13.8 is what does not.

### 13.1 A realm is the user-population boundary

The `Realm` entity is deliberately small: a `Name` an administrator can rename freely, a `Slug` that
is unique and **immutable**, a nullable `LoginRouteId` (ADR-0023; it was `AuthHost` until then), and
`IsSystem`. The slug is immutable because it
is the value of the `realm` claim in every assertion the realm's applications receive — renaming it
would silently change what an upstream is told about the population an account belongs to, which is
a different kind of change from renaming a label in a UI. The migration seeds exactly one row: the
**operator realm** (id 1, slug `operator`), which is the realm every pre-realm row was backfilled
into, the fallback an unrecognised host resolves to, and the population that administers the
instance. It cannot be deleted. Its login page is served on whichever Watchtower route it names, and
falls back to the configured `Auth:Host` when it names none — the escape hatch for an instance whose
hostname is terminated by somebody else's proxy (ADR-0023).

`User.RealmId`, `Group.RealmId` and `StackTemplate.RealmId` all default to the system realm, so a
deployment that never creates a second realm behaves exactly as it did before realms existed. All
three foreign keys are `Restrict`: `realms.delete` refuses while a realm still holds accounts,
groups or categories rather than cascading, because deleting a population would otherwise take every
credential and every tenant stack with it in one call. Emptying it first is the same work made
visible, and each step is separately auditable.

Uniqueness moved with the columns — `(realm_id, normalized_user_name)` for users and
`(realm_id, normalized_name)` for groups, so two populations may each have an `admin` and neither
can see the other's. Identity's own duplicate-name check runs through `WatchtowerUserStore`, which
filters on the ambient `IRealmContext`, so the realm scoping is one filter rather than a rule every
call site has to remember. Two names stayed global, for reasons that are not laziness:
`routes.domain`, because a domain is a global resource — DNS and Caddy's site blocks have no notion
of realms, so two realms claiming one host could not both be served — and `stack_templates.name`,
because a category is picked by name in the one management surface there is, and that surface is
operator-realm-only (§13.6), so a flat namespace is what an operator actually sees.

Routes carry no realm column at all. A route's realm is its stack's category's, and a standalone
stack's routes are the operator realm's. The category is where a population is decided; copying it
onto every tenant route would be one more thing that can disagree with it.

### 13.2 One place a realm is decided, and it fails *safe*

`RealmResolver` answers all three realm questions — which population a host belongs to, which one a
route serves, which host a realm logs in on — for the same reason `RouteAccessPolicy` exists: three
surfaces answering separately would eventually answer differently, and a disagreement about which
population a visitor is in is a hole rather than a bug.

Its failure direction is deliberate and is the opposite of the rest of the auth code: an
**unrecognised host resolves to the operator realm**, so a request arriving on the management port,
by IP, or on a hostname no Watchtower route claims lands on the operator login instead of nowhere. Guessing
wrong here costs a lockout, and the operator population is the one that can always fix things. It
costs nothing in isolation, because an account still only ever authenticates within its own realm —
resolving to the system realm decides which login page a visitor sees, not who gets in.

### 13.3 Realm = cookie jar = login route *(revised by ADR-0023)*

`__wt_sso` is host-scoped, so the host a login page is served on *is* the realm's cookie jar. Login
therefore resolves the realm from the host it was served on and pins `IRealmContext` before touching
`UserManager`: a login page can only ever authenticate its own population.

**That host is a route row.** A realm's `LoginRouteId` names one of its `Target == Watchtower` routes,
and `RealmResolver.LoginHostForAsync` reads its domain. `ResolveByHostAsync` runs the same lookup in
reverse: a Watchtower route claiming the inbound host names the realm, and anything else — the
published port, a bare IP, an application's own domain — resolves to the operator realm, which is the
fail-*safe* direction (the class remarks say why).

The invariant is unchanged and is now structural: **no realm's login host sits behind its own gate.**
`ck_routes_target` refuses to store a Watchtower route that is not `Public`, `proxy.setAccess` refuses
one outright, and `ProxySiteProjection` never marks one `Protected`. Putting a login page behind the
forward-auth that redirects to that login page is a closed loop whose only exit is the management port.
What is gone is the synthesis that used to sit alongside this: the projection no longer invents sites
from `Auth:Host` and `Realm.AuthHost`, so `ProxySite.Local` is derived from the target and nothing else,
and every served hostname has a row — with a status, a certificate, a DNS check and an audit trail.

`Auth:Host` survives as the **operator realm's fallback**, read only when that realm has no login route:
the case where somebody else's proxy terminates the hostname and no route of ours would be served
anyway. A non-system realm in that position creates a Watchtower route regardless — unserved while our
proxy is off, but still where its login address is written down.

Realm CRUD calls `ApplyAsync()` after commit, best-effort, like the route handlers: a newly designated
login host needs its certificate to start issuing rather than waiting for an unrelated reconcile.

Verify challenges to **the route's realm's** login host. A realm with no login route has no host, and
its protected routes then fail closed with a bare 401 — warned once per realm, keyed by id so a
deleted-and-recreated slug warns again — rather than redirecting somewhere arbitrary.

### 13.4 Tokens: per-realm issuer, always-present `realm` claim, one key pair

`AuthTokenSigner` takes a `RealmIdentity` value rather than a row, so it stays a pure singleton with
no database of its own. `iss` is the realm's login host for a customer realm and, for the operator
realm, exactly the issuer this signer produced before realms existed — apps already pinned to it are
untouched, which is the whole point of not reissuing under a new name. The `realm` claim is
**always** present, so a consumer never has to infer the population from the issuer.

One key pair signs every realm. That is a deliberate simplification, and it has one consequence
worth stating: the issuer alone proves nothing about a subject. Every surface that validates a token
therefore checks both. `TenantDiscovery` pins the issuer to the calling stack's realm and re-checks
the subject; UserInfo — the one surface with no realm in context, since an app presents whatever it
was handed — accepts every known realm issuer and then verifies the resolved account is in the realm
whose issuer was actually presented. An issuer collision is only reachable by pointing `Auth:Host`
at a host a realm already holds — the other order cannot happen, because a login host is a route domain
and those are unique — and it is resolved system-realm-first so the population that administers the
instance is structurally unable to lose it, with the loser named in a warning.

### 13.5 The realm invariant lives in `RouteAccessPolicy`, once

**A protected route only ever admits an account of its own realm, whatever its grants say.** Both
`Authenticated` and `Restricted` carry the check; `Public` is split out of what used to be one
`Allow` branch precisely because a route that asks nobody who they are has no population to compare
against. The rule is one expression shared by `IsAuthorizedAsync` and `AccessibleRouteIdsAsync`, so
the per-route and the bulk answer cannot drift — and the bulk form applies it in bulk (two reads for
the whole candidate set) rather than looping.

A realm mismatch is **indistinguishable from a missing grant**: both entry points return the same
`false`, and every surface above collapses it into the same refusal, which preserves the
anti-enumeration property of the login/continue path (§5). A stale grant left behind by some later
realm change therefore grants nothing rather than crossing the boundary.

Write-time refusals exist too — `groups.setMembers` and `proxy.setAccess` refuse cross-realm
subjects, `templates.update` may move a category to another realm only while it has no tenants
(moving a populated one would re-point every tenant route at another population as a side effect of
a form save) — but they are ergonomics: a membership or grant the access check can never honour
reads like access somebody has. The policy is the control.

### 13.6 The management surface belongs to the operator realm

`SystemRealmAuthorizer` decorates the framework authorizer, so **every handler on every transport**
additionally requires a system-realm principal on top of whatever it declares for itself. Central
rather than per-handler: a rule that must be repeated is a rule a new handler can be written
without, and the handler that forgot it would be the one that hands a customer's account the ability
to administer the instance. Three pass-throughs are deliberate — `[AllowAnonymous]` handlers,
unauthenticated callers (left to the inner authorizer so a 401 stays a 401), and the implicit local
administrator used when `Auth:Enabled=false`, which reports the operator realm so that deployment is
unchanged.

The two SSE streams (deploy output, container logs) are minimal-API endpoints that the handler
pipeline never sees, so they carry the same rule as an ASP.NET policy reading the same claim through
the same `WatchtowerClaims.IsSystemRealm` — one rule, one pair of constants. They are not
additionally Admin-gated: a non-administrator operator account could watch them before, and this is
a realm boundary rather than a re-grading of who may see deploy output.

`IsAdmin` is refused outside the operator realm by the user handlers, and the Admin role is emitted
only for a system-realm account by `WatchtowerClaims.ForUser` and by UserInfo — belt to the
handlers' braces, so a row that acquired the flag by a direct database edit still would not turn
into authority over the instance.

A realm account's session is perfectly valid otherwise: it signs in, holds a cookie, passes
forward-auth for its own applications, calls UserInfo. What it cannot do is anything on the
management API — those are not operations it lacks a permission for, they are operations about the
instance rather than about its population.

### 13.7 Surface

`realms.list/create/update/delete`, all `[RequireRole("Admin")]`. A slug is validated as a stable
identifier and never editable. Since ADR-0023 `realms.create` takes a `loginDomain`, which creates the
Watchtower route and designates it; naming an existing route there is deliberately not offered, since a
Watchtower route carries the realm it serves and none can belong to a realm that does not exist yet.
`realms.update` takes `loginRouteId` — that is where an existing route is designated — with `0` clearing
the designation without deleting the hostname. Uniqueness comes from `routes.domain` — one hostname cannot serve two
populations because it cannot be two routes. The operator realm is renameable, takes a login route like
any other, and is never deletable; no realm is deletable while it still serves Watchtower on a
hostname, because that would silently un-serve it. `users`, `groups` and `templates` gained an optional `realmId` (default: the
operator realm, so existing clients are unaffected) and expose it on their DTOs; `users.list` and
`groups.list` gained an optional realm filter; `proxy.getAccess` reports the route's realm, so a
grant editor can scope its candidates instead of discovering the boundary by being refused.

The admin UI gained a Realms screen and realm columns, filters and selects on the screens that name
a population — shown only when there is more than one realm, since a column whose every cell says
the same word is noise.

### 13.8 Still future work

- **Per-realm password, lockout and MFA-enforcement policy.** Today's are instance-wide `Auth:*` options,
  and two-factor is available to every account but required of none. `Realm`
  is where they hang when they land — the seam is the entity, and it now exists.
- **Per-realm federation config** — the §2.1 login button, per population.
- **Realm-scoped management principals.** The token-authenticated management and app APIs
  (`TemplateManagementGrant`) do not pass through the system-realm gate; their principals are stacks
  rather than accounts and are unchanged in v1. "A product's management principal is scoped to its
  realm, so cross-population provisioning is structurally impossible" is therefore still the design
  intent rather than the implementation.
- **Protocol-level OIDC provider per realm** (authorize endpoint, client registry, consent).
  "Fully integrated" will eventually pressure this; nothing in the realm or theme design blocks it,
  and it is not reached for until something concrete demands it (§2.7).
- **Per-category vanity hosts within one realm**, with the silent hop to the realm host when a
  session already exists. The login-code dance is already a cross-host handover mechanism, so this
  is reuse rather than new machinery when it is wanted — but a category gets a realm today, not a
  host of its own (§12.3).
