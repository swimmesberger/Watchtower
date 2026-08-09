// The login surface. Not JSON-RPC: `/api/auth/login` has to work before the caller is allowed to call a
// handler at all, so it is a plain endpoint (backend: Watchtower.Api/Endpoints/WatchtowerAuthEndpoints.cs)
// and this file is the matching plain fetch, in the same shape as `lib/api.ts`.
import { apiBase } from './config'

/** Where the login page lives. Kept here so the shell, the router guard and the RPC client agree. */
export const LOGIN_PATH = '/login'

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
}

/** Thrown when the credentials were rejected; the message is the backend's deliberately generic one. */
export class LoginError extends Error {}

/**
 * Signs in and, on success, leaves the `__wt_sso` cookie in place. The cookie is `HttpOnly`, so nothing
 * here ever sees it — `credentials: 'include'` is what makes the browser keep and resend it, and it is
 * required for the Aspire/Vite dev setup where the SPA and the API are different origins.
 */
export async function login(userName: string, password: string): Promise<LoginResult> {
  const response = await fetch(`${apiBase}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ userName, password }),
  })

  if (response.status === 401) {
    const body = (await response.json().catch(() => null)) as { message?: string } | null
    throw new LoginError(body?.message ?? 'Invalid user name or password.')
  }
  if (!response.ok) {
    throw new Error(`Sign-in failed: ${response.status} ${response.statusText}`)
  }

  return (await response.json()) as LoginResult
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
