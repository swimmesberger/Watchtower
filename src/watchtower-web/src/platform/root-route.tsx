// The root route lives in its own file so feature modules can parent their routes to it
// (`getParentRoute: () => rootRoute`) without importing the composition root (which globs the modules).
import { createRootRouteWithContext, redirect } from '@tanstack/react-router'
import type { QueryClient } from '@tanstack/react-query'
import type { SessionCapabilities } from '@/generated/session-client'
import { LOGIN_PATH } from '@/lib/auth'
import { AppShell } from './app-shell'

export interface RouterContext {
  queryClient: QueryClient
  /**
   * The capability snapshot route guards (`redirectUnless`) read. Typed as the concrete
   * `SessionCapabilities` rather than the kernel's `CapabilityReader` so guards can also see `.user` —
   * `redirectUnless` only needs the narrower surface and is satisfied structurally.
   */
  caps: SessionCapabilities
}

export const rootRoute = createRootRouteWithContext<RouterContext>()({
  component: AppShell,
  /**
   * The one gate every route passes through. With `Auth:Enabled` off the backend reports an implicit
   * local administrator, so `isAuthenticated` is true and this never fires — the no-auth deployment
   * behaves exactly as before. UX only: the handlers behind each route enforce their own authorization.
   */
  beforeLoad: ({ context, location }) => {
    if (context.caps.user.isAuthenticated || location.pathname === LOGIN_PATH) return
    throw redirect({
      to: LOGIN_PATH,
      search: location.href === '/' ? {} : { redirect: location.href },
    })
  },
})
