import { createRootRouteWithContext, createRoute, createRouter } from '@tanstack/react-router'
import type { QueryClient } from '@tanstack/react-query'
import { RootLayout } from './components/layout/root-layout'
import { DashboardPage } from './routes'
import { StacksPage } from './routes/stacks'
import { StackNewPage } from './routes/stacks-new'
import { StackDetailPage } from './routes/stacks-detail'
import { RegistriesPage } from './routes/registries'
import { CredentialsPage } from './routes/credentials'
import { SettingsPage } from './routes/settings'

interface RouterContext {
  queryClient: QueryClient
}

const rootRoute = createRootRouteWithContext<RouterContext>()({
  component: RootLayout,
})

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: DashboardPage,
})

interface StacksSearch {
  status?: 'ok' | 'failed'
}

const stacksRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stacks',
  component: StacksPage,
  validateSearch: (search: Record<string, unknown>): StacksSearch => {
    const status = search.status
    return status === 'ok' || status === 'failed' ? { status } : {}
  },
})

const stackNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stacks/new',
  component: StackNewPage,
})

const stackDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stacks/$id',
  component: StackDetailPage,
})

const registriesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/registries',
  component: RegistriesPage,
})

const credentialsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/credentials',
  component: CredentialsPage,
})

const settingsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/settings',
  component: SettingsPage,
})

const routeTree = rootRoute.addChildren([
  indexRoute,
  stacksRoute,
  stackNewRoute,
  stackDetailRoute,
  registriesRoute,
  credentialsRoute,
  settingsRoute,
])

export const router = createRouter({
  routeTree,
  context: { queryClient: undefined! },
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
