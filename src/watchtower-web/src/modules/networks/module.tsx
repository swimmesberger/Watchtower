import { defineModule, contribute } from '@/platform/contributions'
import { stackDetailTabs, infraSections } from '@/platform/points'
import { StackNetworksTab } from './StackNetworksTab'
import { InfraExposureSection } from './InfraExposureSection'
import { InfraNetworksSection } from './InfraNetworksSection'

export const networksManifest = defineModule({
  name: 'Networks',
  when: { module: 'Networks' },
  contributes: [
    contribute(stackDetailTabs, [
      {
        id: 'networks',
        label: 'Networks',
        value: 'networks',
        order: 30,
        component: ({ stack }) => <StackNetworksTab stack={stack} />,
      },
    ]),
    contribute(infraSections, [
      { id: 'infra-exposure', order: 10, component: InfraExposureSection },
      { id: 'infra-networks', order: 30, component: InfraNetworksSection },
    ]),
  ],
})
