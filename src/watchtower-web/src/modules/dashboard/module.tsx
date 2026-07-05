import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { LayoutDashboard } from 'lucide-react'
import { defineModule, contribute } from '@/platform/contributions'
import { sidebarItems, dashboardSections } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'
import {
  ActiveDeploymentsSection,
  StacksGridSection,
  SummarySection,
  UpdateBannerSection,
} from './sections'

// UI-only module: the dashboard aggregates data from several backend modules, so it has no single
// backing [AppModule] and no manifest-level `when`.
export const dashboardManifest = defineModule({
  name: 'dashboard',
  contributes: [
    contribute(sidebarItems, [
      { id: 'dashboard', label: 'Home', icon: LayoutDashboard, to: '/', exact: true, order: 10 },
    ]),
    // The dashboard-owned sections. These interleave with the sibling metrics module's
    // host-health strip (order 10) and resource-usage ranking (order 40) →
    // update(5) · host(10) · summary(20) · active(30) · resource(40) · grid(50).
    contribute(dashboardSections, [
      { id: 'dash-update', order: 5, component: UpdateBannerSection },
      { id: 'dash-summary', order: 20, component: SummarySection },
      { id: 'dash-active', order: 30, component: ActiveDeploymentsSection },
      { id: 'dash-grid', order: 50, component: StacksGridSection },
    ]),
  ],
})

export const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: lazyRouteComponent(() => import('./DashboardPage'), 'DashboardPage'),
})
