// The login surface. Not JSON-RPC: `/api/auth/login` has to work before the caller is allowed to call a
// handler at all, so it is a plain endpoint (backend: Watchtower.Api/Endpoints/WatchtowerAuthEndpoints.cs)
// and this file is the matching plain fetch, in the same shape as `lib/api.ts`.
import { apiBase } from './config'

/** Where the login page lives. Kept here so the shell, the router guard and the RPC client agree. */
export const LOGIN_PATH = '/login'

/**
 * Where an account manages its own credentials. Platform, not a feature module, and kept next to
 * {@link LOGIN_PATH} for the same reason: the shell, the route and the two places that link to it all have
 * to name the same path, and the page must be reachable by *any* signed-in account — including one outside
 * the operator realm, for whom the shell otherwise renders the applications portal instead of routes.
 */
export const ACCOUNT_SECURITY_PATH = '/account/security'

/**
 * The user id the backend reports when authentication is switched off
 * (`ImplicitAdminCurrentUser.LocalUserId`). Such a session is implicit: there is nothing to sign out of,
 * so the shell hides the affordance rather than offering a button that 404s.
 */
export const LOCAL_USER_ID = 'local'

/** What the SPA learns about the account it just signed in as. */
export interface LoginResult {
  userName: string
  isAdmin: boolean
  /**
   * Set only when the sign-in was continuing into a protected app: the URL to hand the session over on
   * that app's own domain. Treat it as opaque — navigate to it, never render or parse it (see
   * {@link continueSession}).
   */
  continueUrl?: string
}

/**
 * What the password step answers for an account that also demands a second factor. No cookie was set and
 * no session exists yet; `mfaToken` names the challenge and is redeemable exactly once, at
 * {@link completeMfaLogin}, within {@link MFA_CHALLENGE_LIFETIME_MS}.
 */
export interface MfaChallenge {
  mfaToken: string
  /** When this client received the challenge — see {@link isChallengeExpired}. */
  issuedAt: number
}

/** What {@link login} produced: a finished sign-in, or a challenge still to answer. */
export type LoginOutcome =
  | { kind: 'signed-in'; result: LoginResult }
  | { kind: 'mfa-required'; challenge: MfaChallenge }

/**
 * How long the backend keeps a pending-MFA record (`AuthSessionService.MfaPendingLifetime`).
 *
 * Duplicated here on purpose. The backend answers a wrong code and an expired challenge with the *same*
 * 401 and the same body — telling them apart would let a caller holding a stolen password learn whether
 * the challenge is still worth grinding. The client, though, knows how long ago it asked, so it can offer
 * the right recovery ("your request timed out, sign in again" rather than "wrong code") without the
 * server ever having to say which it was.
 */
export const MFA_CHALLENGE_LIFETIME_MS = 5 * 60 * 1000

/** Whether the challenge is old enough that a refusal is almost certainly the window having closed. */
export function isChallengeExpired(challenge: MfaChallenge): boolean {
  return Date.now() - challenge.issuedAt >= MFA_CHALLENGE_LIFETIME_MS
}

/** Thrown when the credentials were rejected; the message is the backend's deliberately generic one. */
export class LoginError extends Error {}

/**
 * Thrown when the account is real but may not enter the app it was trying to reach. Distinct from
 * {@link LoginError} because the answer is different: no password will help, so the form is the wrong
 * thing to show.
 */
export class AccessDeniedError extends Error {}

/**
 * Signs in and, on success, leaves the `__wt_sso` cookie in place. The cookie is `HttpOnly`, so nothing
 * here ever sees it — `credentials: 'include'` is what makes the browser keep and resend it, and it is
 * required for the Aspire/Vite dev setup where the SPA and the API are different origins.
 *
 * `redirectUri` is the protected app the visitor was originally going to, passed straight through; the
 * backend validates it against the route table and answers with a `continueUrl` or a 403.
 */
export async function login(
  userName: string,
  password: string,
  redirectUri?: string,
): Promise<LoginOutcome> {
  const response = await fetch(`${apiBase}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ userName, password, redirectUri }),
  })

  return interpretLogin(response, 'Invalid user name or password.')
}

/**
 * Answers the challenge the password step returned, with either an authenticator code or a recovery code,
 * and — on success — leaves the `__wt_sso` cookie in place exactly as a single-factor sign-in would.
 *
 * `redirectUri` is passed again rather than remembered server-side: the challenge record holds one fact
 * (which account may finish signing in), and the access check for a protected app belongs with the session
 * that is actually minted, which is this one. The backend validates the URL against its own route table
 * either way, so nothing is trusted here that was not trusted at the password step.
 */
export async function completeMfaLogin(
  challenge: MfaChallenge,
  proof: { code: string } | { recoveryCode: string },
  redirectUri?: string,
): Promise<LoginOutcome> {
  const response = await fetch(`${apiBase}/api/auth/login/mfa`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ mfaToken: challenge.mfaToken, ...proof, redirectUri }),
  })

  return interpretLogin(response, 'That code is not valid.')
}

/** The response handling both login steps share — they answer with the same three shapes. */
async function interpretLogin(response: Response, rejection: string): Promise<LoginOutcome> {
  if (response.status === 401) {
    const body = (await response.json().catch(() => null)) as { message?: string } | null
    throw new LoginError(body?.message ?? rejection)
  }
  if (response.status === 403) {
    throw new AccessDeniedError(await deniedMessage(response))
  }
  if (!response.ok) {
    throw new Error(`Sign-in failed: ${response.status} ${response.statusText}`)
  }

  const body = (await response.json()) as LoginResult & { mfaRequired?: boolean; mfaToken?: string }
  return body.mfaRequired && body.mfaToken
    ? { kind: 'mfa-required', challenge: { mfaToken: body.mfaToken, issuedAt: Date.now() } }
    : { kind: 'signed-in', result: body }
}

/**
 * The silent-SSO path: exchanges an existing central session for a hand-over URL into `redirectUri`'s
 * app, so a visitor already signed in to Watchtower never sees a second password prompt.
 *
 * The returned URL is built by the backend from the *route table* — a domain Watchtower actually proxies
 * — not from the string passed in here. That is what makes navigating to it safe, and it is also why
 * `redirectUri` itself must never be rendered or navigated to directly: unlike the `?redirect=` parameter
 * (which {@link safeRedirectTarget} pins to this origin), it is by design cross-origin, so the only
 * defensible handling is to let the backend decide whether it names a real app and echo nothing back.
 */
export async function continueSession(redirectUri: string): Promise<string> {
  const response = await fetch(`${apiBase}/api/auth/continue`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ redirectUri }),
  })

  if (response.status === 403) {
    throw new AccessDeniedError(await deniedMessage(response))
  }
  if (!response.ok) {
    throw new Error(`Could not continue to the application: ${response.status} ${response.statusText}`)
  }

  const body = (await response.json()) as { continueUrl: string }
  return body.continueUrl
}

/** The backend's generic denial message, which deliberately does not say *why*. */
async function deniedMessage(response: Response): Promise<string> {
  const body = (await response.json().catch(() => null)) as { message?: string } | null
  return body?.message ?? 'You are not permitted to access that application.'
}

/** Signs the account out everywhere (the backend revokes every session it holds, not just this browser's). */
export async function logout(): Promise<void> {
  await fetch(`${apiBase}/api/auth/logout`, { method: 'POST', credentials: 'include' })
}

/**
 * Resolves the `?redirect=` parameter to a safe same-origin target, or `/`. This is an open-redirect
 * guard on the one value here that an attacker controls: the login page is reached by following a link,
 * so a hostile `redirect` would bounce the visitor to another site *after* they authenticate — the point
 * at which they are most likely to trust the page they land on.
 *
 * Resolution goes through the URL parser rather than string prefix tests, because the browser's parser
 * accepts far more than `//host` as a host-relative URL and every one of these defeats a `startsWith('/')
 * && !startsWith('//')` check:
 *
 *   /\evil.example        backslash — the WHATWG parser treats \ as / in special schemes
 *   /\/evil.example       mixed separators
 *   /%09/evil.example     and the raw TAB, LF and CR forms: stripped before parsing, leaving //evil…
 *
 * Parsing against `location.origin` and then demanding the result *stay* on that origin collapses all of
 * them: whatever the parser makes of the input is what the browser would have navigated to, so if that
 * lands off-origin it is rejected. Do not "simplify" this back into string checks.
 */
export function safeRedirectTarget(candidate: string | undefined): string {
  if (!candidate) return '/'
  try {
    const resolved = new URL(candidate, window.location.origin)
    if (resolved.origin !== window.location.origin) return '/'
    // Returning to the login page would either loop or show a signed-in visitor the form again.
    if (resolved.pathname === LOGIN_PATH) return '/'
    return `${resolved.pathname}${resolved.search}${resolved.hash}`
  } catch {
    // An input the parser rejects outright is not a target worth guessing at.
    return '/'
  }
}

/**
 * Leaves the SPA for the login page with a full document load.
 *
 * A soft navigation would keep the boot-time capability snapshot and the contribution registry built from
 * it (both are resolved once per boot in `main.tsx`), so the app would carry a stale identity across the
 * sign-in boundary. Reloading is the only way to rebuild both, and it is also what makes this safe to call
 * from the RPC transport, which has no router.
 */
export function goToLogin(returnTo?: string): void {
  const target = returnTo ?? `${window.location.pathname}${window.location.search}`
  const query = target && target !== '/' ? `?redirect=${encodeURIComponent(target)}` : ''
  window.location.assign(`${LOGIN_PATH}${query}`)
}
