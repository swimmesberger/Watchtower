# Central Authorization — Access Control Plane for Proxied Webapps

> Status: Phase 1 implemented on branch `wt/watchtower-central-auth-84057b` (WI-1..WI-6). Phase 2 has begun: **groups + group-based grants are implemented** (`Groups` module, group subjects on `RouteAccessGrant`, group forwarding in the JWT and the per-mode ecosystem headers); OIDC upstream, template policy inheritance and MFA remain future work. §12 (product-branded login pages/themes) and §13 (native multi-realm direction) designed 2026-08-10, not yet implemented.
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

- `__wt_sso` — the central SSO session, host-scoped to the auth host. Established at login.
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
upgrade). First-run bootstrap: if no user exists, create `admin` with the password from
`WATCHTOWER__AUTH__BOOTSTRAPPASSWORD`, else generate one and print it to the log — no interactive
step, no SMTP. Forward-auth for apps additionally requires `Proxy:Enabled` (the verify path is
meaningless without Caddy); Watchtower's own login works with the proxy off.

### 2.7 Scope discipline — what we are *not* building

- **No OIDC/SAML *provider* — for now, and the framing has changed (2026-08-10, see §13).**
  Originally this read "that is exactly the case we delegate to Keycloak". That delegation story is
  retired: Watchtower is the identity home and must grow real IdP capabilities (multiple realms)
  natively; external IdPs (Keycloak, Entra, …) are a **per-realm federation feature for customers
  who already run one** — the §2.1 login button — not Watchtower's growth path. What *survives* of
  this exclusion is the protocol scope: Watchtower does not expose an authorize endpoint / client
  registry / consent flow until a concrete integration needs one. Forward-auth + JWT covers the
  product fleet; the realm design (§13) must not preclude a protocol-level provider later.
- **No self-registration, invites, or email flows.** Admin creates users; admin resets passwords.
- **No MFA in v1** (TOTP/passkeys are v2 — the security-stamp plumbing from Identity core makes
  this a bounded addition).
- **No service tokens in v1**, but the verify endpoint is designed for them (§5): per-route
  **bypass paths** cover webhooks/health endpoints now; `Authorization: Bearer <service-token>`
  slots into verify later without a flow change.

### 2.8 Staging

- **Phase 1 — the control plane:** users + sessions, native Watchtower login (app #0), per-route
  policy, forward-auth + redirect dance + JWT, bypass paths, audit events.
- **Phase 2 — identity federation & groups:** ~~groups + group-based grants~~ *(done)*; generic OIDC
  upstream (= Keycloak, as per-realm federation per §13), template policy inheritance for tenants, TOTP.
- **Phase 3 (as needed):** service tokens, SCIM; branded login (§12) climbs its own ladder
  (tokens → per-category auth hosts → runtime themes) as products demand it; realms (§13) land
  when a second user population actually exists — until then only the seams are kept open.

## 3. Data model

Conventions mirror the existing entities (`Entities/*.cs` + `[EntityConfiguration]` classes,
`int Id` keys, snake_case tables, enums via `HasConversion<string>()`, DbSets from
`[GenerateDbSets]`), then one EF migration via the design-time factory.

```csharp
public sealed class User {
    public int Id { get; set; }
    public required string UserName { get; set; }        // unique (normalized column alongside)
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

public sealed class Group {                              // a named set of accounts
    public int Id { get; set; }
    public required string Name { get; set; }            // printable ASCII, no comma (see below)
    public required string NormalizedName { get; set; }  // unique (the User.NormalizedUserName precedent)
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

public sealed class AuthEvent {                          // audit trail (login, denial, policy change)
    public int Id { get; set; }
    public required string Kind { get; set; }            // login.ok / login.failed / access.denied / …
    public int? UserId { get; set; }
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
   `https://{authHost}/login?redirect_uri={original URL}` (Caddy forwards the auth response to the
   client on non-2xx, so the redirect just works). Everything else (XHR, POST) → plain **401** —
   never redirect a POST into a login page.
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

## 6. Caddy config changes

`CaddySite` gains `bool Protected` (+ bypass data as needed); `CaddyConfigBuilder.Build` emits for
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

One new requirement: the **auth host** must itself be reachable through Caddy — i.e. Watchtower
needs a route to itself (`Auth:Host`, e.g. `watchtower.example.com`, emitted as a site block with
upstream `watchtower:8080`). This also finally gives the Watchtower UI TLS through its own proxy;
the published port stays as the escape hatch (and is how you recover from a proxy misconfiguration).

## 7. Backend surface

**HTTP endpoints** (in `Watchtower.Api/Endpoints`, next to the webhook/SSE endpoints, per the
existing convention for non-RPC/external surfaces):

| Endpoint | Purpose |
|---|---|
| `POST /api/auth/login` | Password login (SPA form); sets `__wt_sso` (+ native Watchtower session). Rate-limited + Identity lockout. Accepts optional `redirect_uri` to continue the dance. |
| `POST /api/auth/logout` | Global sign-out (revokes SSO + all app sessions). |
| `GET /api/access/verify` | The `forward_auth` target (§5). Internal (control network). |
| `GET /api/access/userinfo` | OIDC UserInfo (Core §5.3): identity claims for a Bearer JWT or the app-session cookie. |
| `GET /.watchtower/callback` | Code redemption on the app domain (§5). |
| `GET /.watchtower/logout` | Per-app sign-out. |
| `GET /api/auth/jwks` | Public JWKS for `X-Watchtower-Jwt` verification. |

**Modules** (normal Elarion handlers, JSON-RPC):

- `Users` module: `users.list/create/update/delete/resetPassword/setDisabled` — all
  `[RequireRole("Admin")]`.
- `Groups` module: `groups.list/create/rename/delete/getMembers/setMembers` — all
  `[RequireRole("Admin")]`, because putting an account in a group grants it every route that group is
  named on. `setMembers` is a whole-set replace, reconciled like `proxy.setAccess`.
- `Access` handlers (fold into the existing `Proxy` module — policy is route-scoped):
  `proxy.getAccess` / `proxy.setAccess` (mode + grants + bypass paths), calling
  `CaddyManager.ApplyAsync()` on change like the route CRUD does.
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
- **Users page** (new `users` frontend module, gated `when: { module: 'Users' }` + admin role).
- **Groups page** (new `groups` frontend module, gated the same way): group CRUD plus a members
  roster over the account list.
- **Route form** gains an *Access* section: mode selector (`Public` / `Any authenticated user` /
  `Selected users and groups`), user picker, group picker, bypass-path list.
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
`issuer+sub` linking), template access inheritance, TOTP.

## 11. Risks / open questions

- **Auth-host bootstrap:** the login page must be reachable *before* auth works — `Auth:Host`
  requires DNS + the self-route to be set up first. Mitigation: the UI walks the operator through it
  (create self-route → verify cert → then allow enabling forward-auth on other routes).
- **Locked-out operator:** wrong policy on the Watchtower self-route could lock the admin out of the
  UI. The published port + native login always works; document it as the recovery path. A
  `WATCHTOWER__AUTH__RESETPASSWORD` env hook (applied at startup) is the break-glass.
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
2. **Per-category auth hosts**: optional `StackTemplate.AuthHost` (`login.product-a.com`);
   `CaddyManager` emits one force-unprotected self-route per configured auth host (same mechanism
   as today's single `Auth:Host`); the `/api/access/verify` 302 picks the login host from the
   route's template instead of the global `Auth:Host`. Each host is its own cookie jar (§13:
   this is also the realm boundary mechanism).
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

## 13. Native multi-realm direction *(decided 2026-08-10, seams only — no construction yet)*

**Decision: Watchtower must support multiple realms natively.** External IdPs (Keycloak, Entra) are
a **per-realm federation feature** for customers who already manage an IdP — the §2.1 login button —
*not* Watchtower's exit ramp for growing identity needs. Watchtower is the fully-integrated
identity home. (This inverts the original §2.7 delegation framing; §2.7 has been updated.)

**A realm is the user-population boundary:** its own credential space (username/email uniqueness
scoped *per realm*, not global), its own SSO scope, its own login policies, and eventually its own
federation config and registration/MFA settings. Model shape when it lands: a `Realm` entity,
`User.RealmId`, `StackTemplate.RealmId` (a category lives in exactly one realm), and
realm-consistency enforced on grants (a route only grants users from its realm). Realms also give
the public management API its natural tenancy boundary: a product's management principal is scoped
to its realm, and cross-population provisioning is structurally impossible.

**SSO scope = cookie jar = auth host.** Each realm gets a primary auth host where `__wt_sso` lives —
the §12.3 per-category auth host is the same mechanism wearing a branding costume. Per-category
vanity hosts *within* one realm silently hop to the realm host when a session exists; the
login-code dance is already a cross-host handover mechanism, so this is reuse, not new machinery.

**Seams to keep open now (this is the actionable part):**

1. **Uniqueness constraints:** never let new code assume "username/email is globally unique" as an
   invariant beyond the index itself — the indexes must be able to become `(RealmId, …)`-scoped in
   one migration.
2. **Token shape:** keep `AuthTokenSigner` issuer/claims arranged so realm can surface later as an
   `iss` variant or a claim without breaking existing JWT consumers (the WI-8 per-route header
   modes made consumers explicit, which helps).
3. **Per-population settings hang on the right entity:** anything genuinely per-population —
   password policy, lockout settings, future MFA config — is written as "belongs to realm,
   currently one implicit realm", not as global config.

**Explicitly not decided:** protocol-level OIDC provider per realm (authorize endpoint, client
registry, consent). "Fully integrated" will eventually pressure this too; nothing in the realm or
theme design blocks it, and it is not reached for until something concrete demands it (§2.7).
