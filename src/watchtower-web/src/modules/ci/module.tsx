import { defineModule, contribute } from '@/platform/contributions'
import { stackDetailTabs } from '@/platform/points'
import { StackCiTab } from './StackCiTab'

export const ciManifest = defineModule({
  name: 'Ci',
  when: { module: 'Ci' },
  contributes: [
    contribute(stackDetailTabs, [
      {
        id: 'ci',
        label: 'CI',
        value: 'ci',
        order: 30,
        component: ({ stack }) => <StackCiTab stack={stack} />,
      },
    ]),
  ],
})
