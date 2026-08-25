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
        order: 30,
        component: ({ product }) => <ProductCiTab product={product} />,
      },
    ]),
  ],
})
