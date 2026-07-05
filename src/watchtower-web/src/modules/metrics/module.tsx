import { defineModule, contribute } from '@/platform/contributions'
import { dashboardSections, containerCardExtras } from '@/platform/points'
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
  ],
})
