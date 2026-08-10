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
- Signed identity forwarded to upstreams (convenience headers + an ES256 JWT with a public JWKS).
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

On an allowed request, the proxy forwards three response headers to the upstream (and strips any
inbound copies first, so a client cannot spoof them):

| Header | Contents |
| --- | --- |
| `X-Watchtower-User` | The signed-in user name. |
| `X-Watchtower-Email` | The user's email, when set. |
| `X-Watchtower-Jwt` | A short-lived (5 min) ES256 assertion: `sub`, `email`, `iss`, `aud` (the app's domain), `iat`/`exp`. |

Apps that trust the network topology can read the convenience headers directly. Apps that want a
cryptographic guarantee verify the JWT against the public key set at **`GET /api/auth/jwks`** (cacheable;
it changes only when the signing key does) and check that `aud` is their own domain. The per-stack
ingress networks already make the upstream unreachable except through Caddy — the JWT is defense in
depth on top of that, and the SSO assertion an app with its own login can consume.

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
  ([design.md §2.8](design.md)).
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
