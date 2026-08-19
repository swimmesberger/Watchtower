import { defineModule, contribute } from '@/platform/contributions'
import { stackDetailTabs } from '@/platform/points'
import { StackBackupsTab } from './StackBackupsTab'

export const backupsManifest = defineModule({
  name: 'Backups',
  when: { module: 'Backups' },
  contributes: [
    contribute(stackDetailTabs, [
      {
        id: 'backups',
        label: 'Backups',
        value: 'backups',
        order: 25,
        component: ({ stack }) => <StackBackupsTab stack={stack} />,
      },
    ]),
  ],
})
