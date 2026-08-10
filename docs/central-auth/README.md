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
- Watchtower's own login, on its published port or through its own proxy route.
- Per-route access policy — **Public**, **Authenticated**, or **Restricted** — enforced by Caddy
  `forward_auth`, with the full cross-domain redirect dance so it works on custom domains too.
- Signed identity forwarded to upstreams — an ES256 JWT (always, with a public JWKS) plus opt-in
  per-route plaintext identity headers, and an OIDC UserInfo endpoint.
- Per-route **bypass paths** for webhooks/health endpoints, a first-run admin bootstrap, and a
  break-glass recovery hook.

Not yet (Phase 2): OIDC/SSO upstream, groups, MFA/TOTP, and service tokens. See
[design.md §2.7–§2.8](design.md).

## Enabling it

Set the environment variables (or the matching `Watchtower:Auth:*` config keys):

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

## Per-app access modes

Access policy attaches to a **route** (the domain, as users experience the app). Set it in the UI:
**Routes → the route → Access**.

| Mode | Behavior |
| --- | --- |
| **Public** | No access control. The app is proxied exactly as today; use it for apps with their own login. No `forward_auth` is emitted. |
| **Authenticated** | Any signed-in Watchtower user may enter. |
| **Restricted** | Only users you explicitly grant. Pick them in the same Access dialog. |

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
| **Remote** | Authelia/Traefik names: `Remote-User`, `Remote-Name`, `Remote-Email` (email only when the account has one). |
| **AuthRequest** | oauth2-proxy names: `X-Auth-Request-User`, `X-Auth-Request-Preferred-Username`, `X-Auth-Request-Email` (email only when set). |

There is no bespoke `X-Watchtower-User`/`-Email` header — an off-the-shelf app does not recognise a
made-up name, so the plaintext modes speak the names the Authelia and oauth2-proxy ecosystems already
do.

On every protected route (all modes, including **None**) the proxy strips the **full** identity/authz
namespace of both ecosystems from the inbound request before forwarding — a superset of what it ever
sets, including the group headers (`Remote-Groups`, `X-Auth-Request-Groups`), the oauth2-proxy
access-token header, and the `X-Forwarded-User`/`-Email`/`-Groups`/`-Preferred-Username` family. So a
client can never forge one (e.g. `Remote-Groups: admins` cannot reach a group-aware app as
authoritative). The transport `X-Forwarded-For`/`-Proto`/`-Host` are deliberately left intact.

The per-stack ingress networks already make the upstream unreachable except through Caddy — the JWT is
defense in depth on top of that, and the SSO assertion an app with its own login can consume.

### UserInfo endpoint

For rich or on-demand identity, Watchtower exposes an OIDC UserInfo endpoint (OpenID Connect Core 1.0
§5.3): **`GET /api/access/userinfo`** on the auth host, plus the same handler mounted at
**`/.watchtower/userinfo`** on every protected app's own domain (for browser same-origin calls). It
authenticates the caller two ways:

- `Authorization: Bearer <X-Watchtower-Jwt>` — an app presenting the assertion it received.
- the `__wt_access` cookie — the browser same-origin path.

On success it returns standard OIDC claim JSON — `sub`, `preferred_username`, `email` (when set), and
`roles` (only for admins). With no acceptable credential it answers `401` with
`WWW-Authenticate: Bearer error="invalid_token"`. Groups are Phase 2 and not emitted.

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

## Known limitations

- **No MFA, OIDC/SSO, or groups yet.** Local password accounts only; these are Phase 2
  ([design.md §2.8](design.md)). No group header is forwarded and none is emitted by UserInfo — today
  a protected route forwards only the JWT plus, when a header mode is chosen, that mode's fixed
  plaintext name set.
- **No audit-viewing UI.** Every login, denial, policy change, and break-glass recovery is written to
  an `AuthEvent` row, but v1 ships no screen or query API over them — read the table directly if you
  need the trail. A viewing surface is a planned follow-up.
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
