import { defineModule, contribute } from '@/platform/contributions'
import { stackDetailTabs, infraSections } from '@/platform/points'
import { StackVolumesTab } from './StackVolumesTab'
import { InfraVolumesSection } from './InfraVolumesSection'

export const volumesManifest = defineModule({
  name: 'Volumes',
  when: { module: 'Volumes' },
  contributes: [
    contribute(stackDetailTabs, [
      {
        id: 'volumes',
        label: 'Volumes',
        value: 'volumes',
        order: 20,
        component: ({ stack }) => <StackVolumesTab stack={stack} />,
      },
    ]),
    contribute(infraSections, [{ id: 'infra-volumes', order: 20, component: InfraVolumesSection }]),
  ],
})
