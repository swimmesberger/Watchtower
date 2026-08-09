// The login route is platform, not a feature module: it has to be reachable before any capability gate
// would let a module route render, so it is registered here and carries no `when` clause.
import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { LOGIN_PATH } from '@/lib/auth'
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
  component: lazyRouteComponent(() => import('./LoginPage'), 'LoginPage'),
})
