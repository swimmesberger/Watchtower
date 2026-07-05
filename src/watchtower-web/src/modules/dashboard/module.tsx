import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { LayoutDashboard } from 'lucide-react'
import { defineModule, contribute } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// UI-only module: the dashboard aggregates data from several backend modules, so it has no single
// backing [AppModule] and no manifest-level `when`.
export const dashboardManifest = defineModule({
  name: 'dashboard',
  contributes: [
    contribute(sidebarItems, [
      { id: 'dashboard', label: 'Home', icon: LayoutDashboard, to: '/', exact: true, order: 10 },
    ]),
  ],
})

export const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: lazyRouteComponent(() => import('./DashboardPage'), 'DashboardPage'),
})
