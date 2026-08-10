import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { LineChart } from 'lucide-react'
import { defineModule, contribute } from '@/platform/contributions'
import { dashboardSections, containerCardExtras, sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'
import { HostHealthSection } from './HostHealthSection'
import { ResourceUsageSection } from './ResourceUsageSection'
import { ContainerMetricsRow } from './ContainerMetricsRow'

export const metricsManifest = defineModule({
  name: 'Metrics',
  when: { module: 'Metrics' },
  contributes: [
    contribute(dashboardSections, [
      { id: 'metrics-host', order: 10, component: HostHealthSection },
      { id: 'metrics-resource', order: 40, component: ResourceUsageSection },
    ]),
    contribute(containerCardExtras, [
      { id: 'metrics-container', order: 10, component: ContainerMetricsRow },
    ]),
    // Desktop-only nav entry to the historical view (the live strip stays on the Dashboard). Gated on the
    // `metrics-history` session flag (ADR-0030): the item only renders when the active metrics backend can
    // answer historical ranges (the sqlite default or influxdb, ADR-0013) — on the memory backend it
    // disappears. A runtime backend switch reloads the page, which rebuilds this registry.
    contribute(sidebarItems, [
      {
        id: 'metrics-history',
        label: 'History',
        icon: LineChart,
        to: '/metrics/history',
        order: 25,
        mobile: false,
        when: { flag: 'metrics-history' },
      },
    ]),
  ],
})

export const metricsHistoryRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/metrics/history',
  component: lazyRouteComponent(() => import('./MetricsHistoryPage'), 'MetricsHistoryPage'),
})
