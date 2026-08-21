# Central authorization

Watchtower can manage **users** and decide, **per proxied app, who may reach it** — enforced at the
built-in reverse proxy, the way Cloudflare Access works. An unauthenticated request to a protected app
is redirected to a central login page; after login the visitor lands back on the app with a session,
and the proxy forwards verified identity to the upstream. Apps that do their own auth are passed
through untouched.

Turning this on also makes **Watchtower's own UI a protected app**: the login page, role-gated
navigation, and per-handler authorization all come alive.

The feature is **opt-in and off by default** — an upgrade cannot lock you out. When it is off,
Watchtower is unauthenticated and belongs behind an authenticating reverse proxy, exactly as before.

- Design & rationale: [design.md](design.md)

## What ships today (v1)

- Local user accounts (name + password), managed from the UI. No self-registration, invites, or email.
- **Realms** — separate user populations, each with its own credential space and its own login page, the
  way a Keycloak realm works. Every install has one built in (the *operator* realm); more are optional.
- Watchtower's own login, on its published port or through its own proxy route.
- Per-route access policy — **Public**, **Authenticated**, or **Restricted** — enforced by Caddy
  `forward_auth`, with the full cross-domain redirect dance so it works on custom domains too.
- Signed identity forwarded to upstreams — an ES256 JWT (always, with a public JWKS) plus opt-in
  per-route plaintext identity headers, and an OIDC UserInfo endpoint.
- **Groups** — named sets of accounts that a route can be granted to in one go, and whose names are
  forwarded to apps so they can map them onto their own roles.
- Per-route **bypass paths** for webhooks/health endpoints, a first-run admin bootstrap, and a
  break-glass recovery hook.
- An **Audit** screen over the instance's one trail — every login, denial, policy change and break-glass
  recovery, next to what Watchtower itself did (proxy writes, backups, settings changes) — newest first,
  filterable by category, action and actor.
- **Two-factor authentication (TOTP)** — self-service per account, in any realm: an authenticator app
  plus ten single-use recovery codes, with an administrative reset for the account that loses both.

Not yet (Phase 2): OIDC/SSO upstream, template policy inheritance, and service tokens. See
[design.md §2.7–§2.8](design.md).

## Enabling it

The recommended path is **Settings → Authentication** in the UI: create an enabled admin account on
the Users page first (the page is fully functional while auth is off), flip the toggle, and restart —
`Auth:Enabled` shapes the request pipeline at startup, so it is the one setting that needs a restart
(the UI says so). The handler refuses to enable while no enabled admin exists, so the restart cannot
land on a login page nobody can pass. The login host and session lifetimes are editable there too and
apply live.

Alternatively pin the settings via environment variables — env vars win over UI-edited settings
([ADR-0014](../decisions/0014-env-wins-runtime-settings.md)), which also means
`WATCHTOWER__AUTH__ENABLED=false` + restart always disables authentication, whatever the UI stored —
the escape hatch if you ever lock yourself out:

```yaml
environment:
  WATCHTOWER__AUTH__ENABLED: "true"
  WATCHTOWER__AUTH__BOOTSTRAPPASSWORD: "choose-a-strong-one"   # optional; omit to get a logged random one
  # Only needed to protect OTHER apps (not required for Watchtower's own login):
  WATCHTOWER__AUTH__HOST: watchtower.example.com               # the hostname the login page is reachable on
  # Optional tuning:
  # WATCHTOWER__AUTH__COOKIESECURE: "Auto"                     # Auto | Always | Never (default Auto)
  # WATCHTOWER__AUTH__KEYPATH: "/data/auth-keys"               # signing + data-protection keys (persist this!)
  # WATCHTOWER__AUTH__LOGINRATELIMITPERMINUTE: "10"            # per-IP login backstop
  # WATCHTOWER__AUTH__SESSIONLIFETIMEHOURS: "12"               # idle lifetime (sliding)
  # WATCHTOWER__AUTH__ABSOLUTESESSIONLIFETIMEDAYS: "7"         # hard cap regardless of activity
```

Protecting **other apps** additionally requires the reverse proxy (`WATCHTOWER__PROXY__ENABLED=true`,
see [../reverse-proxy/README.md](../reverse-proxy/README.md)) and an `Auth:Host` that is itself a route
pointing back at Watchtower — the login page has to be reachable through the proxy. Watchtower's **own**
login works with the proxy off, on its published port.

### First-run admin bootstrap

On the first start with auth enabled and no users yet, Watchtower creates an `admin` account:

- If `WATCHTOWER__AUTH__BOOTSTRAPPASSWORD` is set, that becomes the password (a log line confirms it,
  never printing the value).
- If it is unset, a strong random password is generated and **logged once** — this is the only time it
  is shown, so capture it:

  ```
  warn: Created the initial Watchtower administrator 'admin' with the generated password: <value>
        — this is the only time it is shown, so save it now, then change it or set
        WATCHTOWER__AUTH__BOOTSTRAPPASSWORD.
  ```

Passwords must be at least 10 characters; there are no forced symbol/case rules (length beats
composition). Five failed logins park an account for 15 minutes (Identity lockout).

### Break-glass recovery (locked out)

Two things always work if you lock yourself out — for example by setting the wrong policy on
Watchtower's own route:

1. **The published port + native login.** Watchtower is normally reached on its published port, not
   through its own proxy, so a bad proxy/route change never takes the UI away. This is the primary
   recovery path — keep the port published.
2. **`WATCHTOWER__AUTH__RESETPASSWORD`.** Set it and restart: every start then guarantees an `admin`
   account whose password is that value, with any lockout cleared and existing sessions revoked
   (recovery is only recovery if it also ends whatever sessions are already out there). It recreates
   the account if it was renamed or deleted, and takes precedence over `BOOTSTRAPPASSWORD`. **Remove
   it once you are back in** — it re-applies on every start. Each recovery is recorded in the audit
   trail (`auth.breakglass`) as well as the log.

A reset that violates the password policy is refused *before* anything is written, so a bad value
leaves the existing password working rather than wiping it.

## Realms

A **realm** is a user population and everything scoped to one — much like a Keycloak realm. Each realm
has its own credential space (user and group names are unique *within* a realm, so two realms may each
have an `admin` and neither can see the other's), its own login page on its own hostname, and its own
SSO scope. Accounts never cross: a visitor signed into one realm is simply an anonymous visitor to
another realm's apps.

Every install has exactly one built-in realm — the **operator** realm — created for you on upgrade,
holding every account, group and template that existed before realms did. It is the realm that
administers this Watchtower, and it cannot be deleted. If you only ever run one population, you can
ignore this whole section: nothing changes for you.

### Creating a realm

**Realms → New**, then:

- **Name** — a display label. Rename it whenever you like.
- **Slug** — a stable lowercase identifier (letters, digits, single hyphens). It is **fixed at creation
  and never editable**, because it travels to your applications as the `realm` claim in every forwarded
  assertion; renaming it would silently change what they are told about who your users are. Pick it as
  carefully as you would a database name.
- **Login host** — the hostname this realm's login page is served on, e.g. `login.acme-corp.com`. Give
  it a DNS record pointing at the machine running the proxy, exactly as you would for any other route;
  Watchtower then serves that host itself and obtains a certificate for it on first request. It must be
  a bare hostname (no scheme, port or path), it must be different from every other realm's, and it may
  not be the Watchtower login host from `WATCHTOWER__AUTH__HOST` — one hostname resolving to two
  populations would make "who administers this instance" ambiguous.

The login host is optional at creation, because DNS usually is not ready yet. A realm without one is a
perfectly good population that simply cannot be logged into: its protected apps answer `401` instead of
redirecting anywhere, and a warning in the log says so once. Fill it in later and the login page starts
working with no other change.

Then place things in the realm: **users** and **groups** are created with a realm (defaulting to
operator), and a **stack template** — a product category — belongs to exactly one realm. A route's realm
is inherited from its stack's template, so every tenant of a category serves that category's population;
routes of a standalone stack (no template) belong to the operator realm. A category may be moved to
another realm only while it has no tenants, since moving a populated one would re-point every tenant
route at a different population as a side effect of a form save.

### How login and SSO work per realm

The hostname the login page is served on decides the population, because the session cookie is
host-scoped — the host *is* the cookie jar. So:

- An anonymous visitor to a protected app is redirected to **that app's realm's** login host, never to
  the operator login. They are only ever shown a login page that could actually admit them.
- After signing in they get silent SSO across every app of that realm, on any domain — and no SSO at
  all into another realm's apps, which is the point rather than a gap.
- A protected route only ever admits an account **of its own realm**, whatever grants say. A grant
  naming somebody from another realm is refused when you try to save it, and would admit nobody even if
  one were left behind by a later change. A refusal for the wrong realm looks exactly like a refusal for
  a missing grant, so the boundary cannot be probed.
- Every assertion forwarded to an app carries a `realm` claim naming the population, and its `iss` is
  the realm's own login host (the operator realm keeps the issuer it always had, so apps already
  verifying tokens are unaffected).

The operator realm is the exception to the host rule: its login page is always served on
`WATCHTOWER__AUTH__HOST`, never on a host stored against the realm row. Authentication must not need a
database row to find its own login page, so that field is read-only for the operator realm in the UI.

Each login host is served as an **unprotected** site block that proxies everything to Watchtower — no
realm's login page may sit behind the gate that redirects to it, or the only way back in would be the
published port. One consequence worth knowing: `GET /api/proxy/ask` (the on-demand-TLS gate Caddy calls
to decide whether to issue a certificate for a domain) is reachable on those hosts too, so it now
answers a bare **404** to anything that arrived through the proxy — the same answer a nonexistent path
gives, identical for known and unknown domains. Caddy calls it directly, without forwarding headers, and
still gets the real answer. Nobody else can use it to enumerate which domains this instance manages.

### Management access is operator-realm-only

The whole management surface — every JSON-RPC method, plus the deploy-output and container-log
streams — requires an account **in the operator realm**, on top of whatever role the operation itself
asks for. A customer realm's account can sign in, hold a session, reach its own applications and call
UserInfo; it can do nothing to this Watchtower. Relatedly, the **Admin** flag can only be set on
operator-realm accounts: the role administers the whole instance, so the UI hides the toggle elsewhere
and the server refuses the pair.

### What a realm user sees

Signing in on a realm's login host lands the visitor on **Your applications** — a plain list of the apps
their account may open, each a link to that app's own domain, where the usual redirect dance signs them
in without a second prompt. There is no sidebar and no management screen, because there is nothing there
they could use. The list holds only routes of **their own realm** that the access policy already admits
them to, so it names nothing they could not have reached by typing the address — in particular, another
realm's public apps are not listed, since that would be exactly the domain enumeration `/api/proxy/ask`
answers `404` to prevent on these same hosts. Several domains pointing at the *same service* are aliases
— one app under a second name — and appear once, as its canonical domain; domains pointing at *different*
services of one stack are separate ways in (a UI and its API) and each get their own entry. An account
that has been granted nothing yet sees a page saying so, and operator-realm accounts land on the
management UI as before.

### Deleting a realm

A realm is deletable only while it holds **nothing** — no accounts, no groups, no templates. There are
no cascades, deliberately: deleting a population would otherwise take every credential and every tenant
stack with it in a single call. Empty it first; each step is separately visible in the audit trail. The
operator realm is never deletable, and every realm create/update/delete is recorded (`realm.created`,
`realm.updated`, `realm.deleted`).

## Per-app access modes

Access policy attaches to a **route** (the domain, as users experience the app). Set it in the UI:
**Routes → the route → Access**.

| Mode | Behavior |
| --- | --- |
| **Public** | No access control. The app is proxied exactly as today; use it for apps with their own login. No `forward_auth` is emitted. |
| **Authenticated** | Any signed-in user **of the route's realm** may enter. |
| **Restricted** | Only users you explicitly grant, and only from the route's realm. Pick them in the same Access dialog. |

**Bypass paths** (Restricted/Authenticated routes): a newline-separated list of rooted path prefixes
that skip the access check entirely — for webhooks, health checks, or non-browser API callers that
can't do the login redirect. Prefixes are matched literally and fail closed: any percent-encoding or
`.`/`..` segment in the request path disqualifies the match, so `/webhooks/` cannot be smuggled past
with `/webhooks/..%2fadmin`. Bypassed requests carry **no** identity headers — a bypass means "no
access control here", not "anonymous access as somebody".

The reserved prefix **`/.watchtower/*`** is always routed to Watchtower (the login callback and per-app
logout), on every protected domain — an app that genuinely wants that prefix cannot have it, the same
trade Cloudflare makes with `/cdn-cgi/`.

## How apps consume identity

On an allowed request the proxy **always** forwards a signed assertion, and — only when the route opts
in — a set of plaintext identity headers under ecosystem-standard names. Either way it first strips any
inbound copies of the whole identity/authz namespace, so a client cannot spoof them.

### The JWT (default, recommended)

Every protected route forwards `X-Watchtower-Jwt`: a short-lived (5 min) ES256 assertion carrying
`sub`, `email` (when set), `iss`, `aud` (the app's domain), and `iat`/`exp`. Apps that want a
cryptographic guarantee verify it against the public key set at **`GET /api/auth/jwks`** (cacheable; it
changes only when the signing key does) and check that `aud` is their own domain. This is the default
and the recommended path — **JWT-only is what a route forwards unless you opt into plaintext headers.**

### Plaintext identity headers (optional, per route)

For off-the-shelf apps that read a trusted username header instead of validating a JWT, a route can
additionally forward plaintext identity headers. Choose the mode in **Routes → the route → Access →
"Identity forwarding"**:

| Mode | Headers forwarded |
| --- | --- |
| **None** (default) | JWT only — no plaintext identity header. |
| **Remote** | Authelia/Traefik names: `Remote-User`, `Remote-Name`, `Remote-Email`, `Remote-Groups` (email and groups only when the account has them). |
| **AuthRequest** | oauth2-proxy names: `X-Auth-Request-User`, `X-Auth-Request-Preferred-Username`, `X-Auth-Request-Email`, `X-Auth-Request-Groups` (email and groups only when set). |
| **Cloudflare** | Cloudflare Access names: `Cf-Access-Authenticated-User-Email` (when the account has an email), plus the Watchtower-signed assertion duplicated under `Cf-Access-Jwt-Assertion`. For apps written against Cloudflare's header contract, so the same stack runs unchanged behind Cloudflare Access (tunnel provider) or behind integrated auth — an app that cryptographically verifies the assertion re-points its JWKS/issuer configuration (env or appsettings) from `{team}.cloudflareaccess.com/cdn-cgi/access/certs` to Watchtower's `/api/auth/jwks`; the header names stay identical. |

The group header carries the account's group names sorted and comma-joined (`admins,platform`), which
is what group-aware apps such as Grafana and Nextcloud expect. It is **omitted entirely** rather than
sent empty when the account is in no group — an empty value reads to some apps as membership of a
group named `""`. Group names are restricted to printable ASCII without commas when you create them,
so this encoding cannot be ambiguous.

There is no bespoke `X-Watchtower-User`/`-Email` header — an off-the-shelf app does not recognise a
made-up name, so the plaintext modes speak the names the Authelia and oauth2-proxy ecosystems already
do.

On every protected route (all modes, including **None**) the proxy strips the **full** identity/authz
namespace of both ecosystems from the inbound request before forwarding — a superset of what it ever
sets, including the group headers, the oauth2-proxy access-token header, and the
`X-Forwarded-User`/`-Email`/`-Groups`/`-Preferred-Username` family it never populates. So a client can
never forge one: a low-privilege user sending `Remote-Groups: admins` has it stripped, and whatever
the route then forwards is derived from their real membership. The transport
`X-Forwarded-For`/`-Proto`/`-Host` are deliberately left intact.

The per-stack ingress networks already make the upstream unreachable except through Caddy — the JWT is
defense in depth on top of that, and the SSO assertion an app with its own login can consume.

### UserInfo endpoint

For rich or on-demand identity, Watchtower exposes an OIDC UserInfo endpoint (OpenID Connect Core 1.0
§5.3): **`GET /api/access/userinfo`** on the auth host, plus the same handler mounted at
**`/.watchtower/userinfo`** on every protected app's own domain (for browser same-origin calls). It
authenticates the caller two ways:

- `Authorization: Bearer <X-Watchtower-Jwt>` — an app presenting the assertion it received.
- the `__wt_access` cookie — the browser same-origin path.

On success it returns standard OIDC claim JSON — `sub`, `preferred_username`, `email` (when set),
`groups` (always present; an empty array when the account is in none), and `roles` (only for
administrators, and only in the operator realm).
Identity is answered *as of now* against a freshly reloaded account, so a membership revoked a moment
ago is already gone here even if an assertion minted minutes earlier still lists it. With no
acceptable credential it answers `401` with `WWW-Authenticate: Bearer error="invalid_token"`.

## Cookies & HTTPS

Session cookies are `HttpOnly`, `SameSite=Lax`, host-scoped (no `Domain`), and hold only a random
token — the server stores a hash, so signing out revokes immediately and a stolen database row is not a
usable cookie.

The **`Secure`** attribute is where deployments differ, controlled by `WATCHTOWER__AUTH__COOKIESECURE`:

- **`Auto`** (default) — `Secure` follows the request scheme *after* `X-Forwarded-Proto` is applied.
  Behind the proxy the browser spoke HTTPS, so the cookie is `Secure`; on the plain-HTTP published port
  it is not (marking it `Secure` there would set a cookie the browser never sends back, breaking the
  recovery path). **This requires the proxy in front to send `X-Forwarded-Proto`** — Watchtower's own
  Caddy does. Behind a different proxy, make sure it forwards that header.
- **`Always`** — force `Secure`. Use when Watchtower is only ever reached over HTTPS and the proxy does
  *not* send `X-Forwarded-Proto`. Note this makes the plain-HTTP published port stop working as a
  recovery path.
- **`Never`** — only for a lab/LAN with no TLS anywhere; the cookie then travels in the clear.

The signing and data-protection keys live under `Auth:KeyPath` (default `/data/auth-keys`). **Keep this
on a persistent volume** — losing it signs everyone out on restart.

## Login rate limiting

`POST /api/auth/login` is throttled per client IP (`LoginRateLimitPerMinute`, default 10) as a coarse
backstop *on top of* the per-account lockout. Over the limit, it answers `429` with a generic body that
reveals nothing about any account.

The partition is the **connection** IP, deliberately not `X-Forwarded-For` (which Watchtower does not
process — trusting it would let a direct client rotate the header and evade the limit). The trade-off:
behind the single reverse proxy every login shares Caddy's address, so the limit is effectively
**instance-global** there; on the published port it is genuinely per-client. Raise the knob on a busy,
multi-user instance reached through the proxy — the per-account lockout is the primary control either
way.

## Two-factor authentication

Any signed-in account can turn it on for itself from **Security** (the shield in the sidebar, and in the
applications portal for realm accounts) — no administrator involvement, and it works in every realm.

- **Enrolling** scans a QR code (or types the key) into any TOTP app, then proves it with a code *and*
  the account password. The password is what makes enrolment an act of the account's owner rather than
  of whoever is holding a signed-in browser; the code alone would be something an attacker also has.
  Two-factor stays off until both match, so an abandoned setup cannot lock anyone out.
- **Recovery codes** — ten, single-use, shown once. Only their SHA-256 hashes are stored, so nothing can
  show them again; redeeming one deletes it. Regenerating replaces the whole set and needs an
  authenticator code specifically (a recovery code is refused, or one leaked code would mint ten more).
- **Signing in** becomes password → code. The password step mints no cookie at all: it answers with a
  single-use challenge token, good for five minutes, that only the second step can redeem. A wrong code
  keeps the challenge (a typo should not mean retyping your password) but spends the same five-attempt
  lockout budget a wrong password does, and the per-IP login limiter covers the step too.
- **Turning it off** takes a code — or a recovery code, so a lost phone does not require an
  administrator. It clears the flag, the key and every remaining code together.
- **If both are lost**, an administrator uses **Reset two-factor** on the Users page (`users.resetMfa`).
  That can only ever *remove* a factor: there is no call that enrols one on somebody's behalf, because
  enrolling needs a code only the owner can produce. It leaves the account's sessions and password
  alone — it exists because someone cannot get in.
- **Break-glass clears it too.** `WATCHTOWER__AUTH__RESETPASSWORD` resets the `admin` password *and*
  removes that account's second factor, because a lost authenticator is the usual reason to reach for
  the hook and a recovery that left the code prompt standing would recover nothing. Anyone who can set
  that variable and restart the process already owns the deployment, so this takes nothing from them
  that they did not have; both the audit trail (`auth.breakglass`) and the startup warning say the
  factor was removed.
- **The trail** gains `mfa.totp.enabled`, `mfa.totp.disabled`, `mfa.totp.reset`,
  `mfa.recovery.generated`, `mfa.recovery.redeemed`, `login.mfa.ok` and `login.mfa.failed`. A
  two-factor sign-in writes `login.mfa.ok` and no `login.ok` — it is one event, not two.

Neither the authenticator key nor a recovery code ever appears in a DTO, an API response or the audit
detail. The one exception is the enrolment response itself, which hands the key to its own owner once.

## Known limitations

- **No OIDC/SSO upstream yet.** Local password accounts only; federation is Phase 2
  ([design.md §2.8](design.md)). Groups have landed — see **Groups** in the sidebar — but group
  membership comes from Watchtower's own directory, not from a federated identity provider.
- **Two-factor cannot be *required*.** It is available to every account and enrolment is entirely
  self-service, but nothing lets an operator make it mandatory — per-realm enforcement lands on the
  `Realm` entity alongside the rest of the per-realm login policy below.
- **Groups are not inherited by tenant routes.** A stack template carries no access policy yet, so a
  route auto-created for a new tenant starts at the default and has to be granted explicitly. Also
  Phase 2 ([design.md §2.8](design.md)).
- **The audit trail is bounded, and only viewable.** Every login, denial, policy change and break-glass
  recovery is an `AuditEvent` row in the instance's one trail, readable from the **Audit** screen (or
  `audit.listEvents` / `audit.listFacets`). Retention keeps the newest 2000 rows **per category** (so
  login volume never evicts the `users` or `backups` history), there is no export, and no way to delete
  a row from the product.
- **Login policy is instance-wide, not per realm.** Password rules, lockout and rate limiting are the
  `Auth:*` settings above and apply to every realm alike; a realm cannot yet have its own. Per-realm
  policy (and per-realm federation to an external IdP) is where realms grow next — see
  [design.md §13.8](design.md).
- **On the published port, the `Host` header picks the realm.** Which population a login attempt goes to
  is decided by the hostname the login page was served on. Behind the proxy that is pinned by DNS and
  TLS, but the published break-glass port takes whatever `Host` a client sends. So somebody who can
  reach that port directly can aim login attempts at *any* realm's credential space. They still need the
  password — this is not a credential bypass, and it grants nothing — but they can drive a known account
  in any realm into the 15-minute lockout. Treat the published port as the trusted-network escape hatch
  the recovery path already assumes it is, rather than as a public entrance.
- **Moving a realm's login host signs its users out.** The SSO cookie is host-scoped, which is exactly
  what makes the host the realm's cookie jar — so changing or clearing a realm's login host orphans every
  session living on the old host, and changes the `iss` its apps see in forwarded assertions. There is no
  session migration; the change is audited (`realm.updated` names the old host and the new) so the reason
  is findable afterwards.
- **`/.watchtower/*` is reserved** on every protected domain and cannot be used by the app itself.
- **Don't publish host ports on a protected app.** Forward-auth is enforced *at the proxy*; the
  per-stack ingress network is what keeps the upstream reachable only through Caddy. An app that also
  publishes its own host port bypasses the proxy — and therefore the access check — entirely.
- **Cookie tossing.** App sessions ride host-scoped cookies. Watchtower does not attempt to defend
  against a fully-attacker-controlled sibling host under a shared parent domain planting a
  `__wt_access` cookie value; because sessions are server-side rows bound to their route and validated
  on every request, a foreign token is rejected rather than honored, so the worst practical outcome is
  a forced re-login, not impersonation.
- **Rotating the JWT signing key** invalidates in-flight assertions only — they live 5 minutes, so the
  blast radius is one verify cycle.
