// The login route is platform, not a feature module: it has to be reachable before any capability gate
// would let a module route render, so it is registered here and carries no `when` clause.
import { createRoute, lazyRouteComponent, redirect } from '@tanstack/react-router'
import { LOGIN_PATH, safeRedirectTarget } from '@/lib/auth'
import { rootRoute } from './root-route'

/** The only search parameter: where to return to once the visitor is signed in. */
export interface LoginSearch {
  redirect?: string
}

export const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: LOGIN_PATH,
  validateSearch: (search: Record<string, unknown>): LoginSearch => ({
    redirect: typeof search.redirect === 'string' ? search.redirect : undefined,
  }),
  // Someone who already has a session — including everyone when Auth:Enabled is off, where the backend
  // reports an implicit local administrator — has no business staring at a sign-in form.
  beforeLoad: ({ context, search }) => {
    if (!context.caps.user.isAuthenticated) return
    throw redirect({ href: safeRedirectTarget(search.redirect) })
  },
  component: lazyRouteComponent(() => import('./LoginPage'), 'LoginPage'),
})
