// The root route lives in its own file so feature modules can parent their routes to it
// (`getParentRoute: () => rootRoute`) without importing the composition root (which globs the modules).
import { createRootRouteWithContext } from '@tanstack/react-router'
import type { QueryClient } from '@tanstack/react-query'
import type { CapabilityReader } from '@swimmesberger/elarion-contributions'
import { AppShell } from './app-shell'

export interface RouterContext {
  queryClient: QueryClient
  /** The capability snapshot route guards (`redirectUnless`) read. */
  caps: CapabilityReader
}

export const rootRoute = createRootRouteWithContext<RouterContext>()({
  component: AppShell,
})
