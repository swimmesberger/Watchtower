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
 * Only same-origin, absolute paths are accepted as a return target — an open-redirect guard for the one
 * value on this page that comes from the URL. `//evil.example` is a protocol-relative URL, hence the
 * second check.
 */
export function safeRedirectTarget(candidate: string | undefined): string {
  if (!candidate || !candidate.startsWith('/') || candidate.startsWith('//')) return '/'
  return candidate
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
