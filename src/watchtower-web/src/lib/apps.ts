// The applications a signed-in visitor may enter. Not JSON-RPC: `/rpc` is the operator population's
// surface (every call from a realm account is Forbidden), and this endpoint exists precisely for the
// accounts that surface refuses — so it is a plain endpoint (backend:
// Watchtower.Api/Endpoints/WatchtowerAccessEndpoints.cs) and this file is the matching plain fetch, in the
// same shape as `lib/auth.ts`.
import { apiBase } from './config'
import { goToLogin, LOGIN_PATH } from './auth'

/** One application the visitor may open. */
export interface AppLink {
  /** The public hostname — the same one that ends up in their address bar. What the card shows. */
  domain: string
  /** A human label for the deployment behind it. Never an identifier. */
  name: string
  /**
   * Where to navigate. Built by the backend from the route's own TLS setting, so a plain-HTTP route is
   * linked as `http` — the client has nothing to derive that from, and guessing `https` would be a
   * connection failure rather than a redirect.
   */
  url: string
}

/**
 * Lists the applications the current session may enter. Every entry is a route the backend has already
 * decided this account is authorized for, so the page can link straight to it — following the link starts
 * the ordinary verify → silent-SSO hand-over, which is what actually admits them.
 *
 * A 401 means the central session went away between boot and now, and is handled exactly as the RPC
 * transport handles its own expiry: bounce to the login page once rather than rendering an error the
 * visitor can do nothing about.
 */
export async function listApps(): Promise<AppLink[]> {
  const response = await fetch(`${apiBase}/api/access/apps`, {
    // Carries the __wt_sso cookie; required cross-origin (Vite dev server / Aspire) and harmless same-origin.
    credentials: 'include',
  })

  if (response.status === 401) {
    if (window.location.pathname !== LOGIN_PATH) goToLogin()
    throw new Error('Your session has expired.')
  }
  if (!response.ok) {
    throw new Error(`Could not load your applications: ${response.status} ${response.statusText}`)
  }

  const body = (await response.json()) as { apps?: AppLink[] }
  return body.apps ?? []
}
