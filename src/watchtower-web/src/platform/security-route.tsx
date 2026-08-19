// Account security is platform, not a feature module: it belongs to whoever is signed in rather than to a
// backend module, it is gated by nothing (every account may protect its own credentials, in any realm),
// and the root route's `beforeLoad` already sends an unauthenticated visitor to the login page — so there
// is no `when` clause and no guard of its own.
import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { ACCOUNT_SECURITY_PATH } from '@/lib/auth'
import { rootRoute } from './root-route'

export const securityRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: ACCOUNT_SECURITY_PATH,
  component: lazyRouteComponent(() => import('./SecurityPage'), 'SecurityPage'),
})
