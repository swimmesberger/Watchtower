# Central Authorization — Access Control Plane for Proxied Webapps

> Status: Phase 1 implemented on branch `wt/watchtower-central-auth-84057b` (WI-1..WI-6). Phase 2 (OIDC upstream, groups, MFA) remains future work.
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
  group headers (`Remote-Groups`, `X-Auth-Request-Groups`) and the `X-Forwarded-*` identity family, so
  nothing reaching the upstream — not even a header we never set — is client-forgeable.
- **A signed JWT** (`X-Watchtower-Jwt`, ES256) carrying `sub`, `email`, `iss`, `aud` (the app's
  domain), `iat`/`exp`, verifiable against Watchtower's JWKS endpoint. Apps that care validate
  cryptographically instead of trusting topology; apps with their own auth can consume it as an SSO
  assertion. The per-stack `watchtower-ingress-{stackId}` networks already guarantee the upstream is
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

- **No OIDC/SAML *provider*.** Watchtower never becomes an IdP other apps integrate against — that
  is exactly the case we delegate to Keycloak. Forward-auth + JWT covers everything else.
- **No self-registration, invites, or email flows.** Admin creates users; admin resets passwords.
- **No MFA in v1** (TOTP/passkeys are v2 — the security-stamp plumbing from Identity core makes
  this a bounded addition).
- **No service tokens in v1**, but the verify endpoint is designed for them (§5): per-route
  **bypass paths** cover webhooks/health endpoints now; `Authorization: Bearer <service-token>`
  slots into verify later without a flow change.

### 2.8 Staging

- **Phase 1 — the control plane:** users + sessions, native Watchtower login (app #0), per-route
  policy, forward-auth + redirect dance + JWT, bypass paths, audit events.
- **Phase 2 — identity federation & groups:** generic OIDC upstream (= Keycloak), groups +
  group-based grants, template policy inheritance for tenants, TOTP.
- **Phase 3 (as needed):** service tokens, per-tenant IdPs, SCIM.

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

public sealed class RouteAccessGrant {                   // subjects allowed when Restricted
    public int Id { get; set; }
    public int RouteId { get; set; }
    public int UserId { get; set; }                      // v2: nullable + GroupId (principal kinds)
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
- **Route form** gains an *Access* section: mode selector (`Public` / `Any authenticated user` /
  `Selected users`), user picker, bypass-path list.
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

**Phase 2** — generic OIDC upstream (JIT provisioning, `issuer+sub` linking), groups + group
grants, template access inheritance, TOTP.

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
