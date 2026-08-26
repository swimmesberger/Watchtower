import { defineModule, contribute } from '@/platform/contributions'
import { productDetailTabs } from '@/platform/points'
import { ProductCiTab } from './ProductCiTab'

export const ciManifest = defineModule({
  name: 'Ci',
  when: { module: 'Ci' },
  contributes: [
    // On the product, not the stack (ADR-0026): runners, toolcache and registry sync are properties
    // of the repository, shared by every instance deploying it. The tab's own copy always said so.
    contribute(productDetailTabs, [
      {
        id: 'ci',
        label: 'CI',
        value: 'ci',
        // 32, not 30: stage 8b's fold puts Instances at 30, which is the slot the Backups tab's
        // comment already reserved for it and the position design.md §"Product detail page" numbers it
        // in (Overview, Releases, Instances, CI, Settings). A tie would have been resolved by module
        // discovery order, which is alphabetical and therefore an accident.
        order: 32,
        component: ({ product }) => <ProductCiTab product={product} />,
      },
    ]),
  ],
})
