import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { LayoutDashboard } from 'lucide-react'
import { defineModule, contribute } from '@/platform/contributions'
import { sidebarItems, dashboardSections } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'
import {
  ActiveDeploymentsSection,
  FleetsSection,
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
    // update(5) · host(10) · summary(20) · active(30) · resource(40) · fleets(45) · grid(50).
    //
    // Fleets sits at 45 rather than 35 so the two card grids are adjacent: the fleets and the stacks
    // they leave behind are one reading ("here is what I run for customers, here is everything else"),
    // and splitting them around the metrics ranking would break it.
    contribute(dashboardSections, [
      { id: 'dash-update', order: 5, component: UpdateBannerSection },
      { id: 'dash-summary', order: 20, component: SummarySection },
      { id: 'dash-active', order: 30, component: ActiveDeploymentsSection },
      {
        id: 'dash-fleets',
        order: 45,
        // A fleet is a *product* with tenancy, so the section has nothing to read without the Products
        // module — and with it off the grid's own fleet lookup is disabled too, leaving the dashboard
        // exactly as it was. (The section is additionally silent whenever no fleet exists, which is
        // what makes it self-adjusting rather than a setting anybody has to find.)
        when: { module: 'Products' },
        component: FleetsSection,
      },
      { id: 'dash-grid', order: 50, component: StacksGridSection },
    ]),
  ],
})

export const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: lazyRouteComponent(() => import('./DashboardPage'), 'DashboardPage'),
})
