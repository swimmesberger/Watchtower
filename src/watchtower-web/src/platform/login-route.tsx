// The login route is platform, not a feature module: it has to be reachable before any capability gate
// would let a module route render, so it is registered here and carries no `when` clause.
import { createRoute, lazyRouteComponent, redirect } from '@tanstack/react-router'
import { LOGIN_PATH, safeRedirectTarget } from '@/lib/auth'
import { rootRoute } from './root-route'

/**
 * Two search parameters, and they are not interchangeable.
 *
 * `redirect` is a path within *this* origin — where to return to once the visitor is signed in to
 * Watchtower's own UI. `redirect_uri` is an absolute URL on a *protected app's* domain, put there by the
 * verify endpoint when it turned an anonymous request away; it is the cross-domain hand-over target and
 * is never treated as a local path, never rendered, and never navigated to directly (see `lib/auth.ts`).
 */
export interface LoginSearch {
  redirect?: string
  redirect_uri?: string
}

export const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: LOGIN_PATH,
  validateSearch: (search: Record<string, unknown>): LoginSearch => ({
    redirect: typeof search.redirect === 'string' ? search.redirect : undefined,
    redirect_uri: typeof search.redirect_uri === 'string' ? search.redirect_uri : undefined,
  }),
  // Someone who already has a session — including everyone when Auth:Enabled is off, where the backend
  // reports an implicit local administrator — has no business staring at a sign-in form. Unless they
  // arrived from a protected app: the session they hold is Watchtower's, and turning it into one for that
  // app still takes a round trip, which the page itself performs.
  beforeLoad: ({ context, search }) => {
    if (!context.caps.user.isAuthenticated || search.redirect_uri) return
    throw redirect({ href: safeRedirectTarget(search.redirect) })
  },
  component: lazyRouteComponent(() => import('./LoginPage'), 'LoginPage'),
})
