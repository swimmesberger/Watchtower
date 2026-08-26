import { defineModule, contribute } from '@/platform/contributions'
import { productDetailTabs, stackDetailTabs } from '@/platform/points'
import { ProductBackupsTab } from './ProductBackupsTab'
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
    // The product half (ADR-0026 stage 7): the fleet policy and how the fleet is doing. Ordered after
    // Instances (30) and before Settings (40) — it is about the instances, and reading it right after
    // the roster is the order the question comes in.
    contribute(productDetailTabs, [
      {
        id: 'backups',
        label: 'Backups',
        value: 'backups',
        order: 35,
        component: ({ product }) => <ProductBackupsTab product={product} />,
      },
    ]),
  ],
})
