import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Package } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { productDetailTabs, sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'
import { OverviewTab } from './OverviewTab'
import { ReleasesTab } from './ReleasesTab'
import { SettingsTab } from './SettingsTab'

export const productsManifest = defineModule({
  name: 'Products',
  when: { module: 'Products' },
  contributes: [
    // Desktop-only: the catalogue answers "what can this box deploy", which is planning work. The
    // mobile tab bar is for status checking, and status belongs to the instances (design.md §UX).
    contribute(sidebarItems, [
      {
        id: 'products',
        label: 'Products',
        icon: Package,
        to: '/products',
        group: 'deploy',
        order: 15,
        mobile: false,
      },
    ]),
    contribute(productDetailTabs, [
      {
        id: 'overview',
        label: 'Overview',
        value: 'overview',
        order: 10,
        component: ({ product }) => <OverviewTab product={product} />,
      },
      {
        id: 'releases',
        label: 'Releases',
        value: 'releases',
        order: 20,
        component: ({ product }) => <ReleasesTab product={product} />,
      },
      {
        id: 'settings',
        label: 'Settings',
        value: 'settings',
        order: 40,
        component: ({ product }) => <SettingsTab product={product} />,
      },
    ]),
  ],
})

export const productsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/products',
  beforeLoad: redirectUnless({ module: 'Products' }, '/'),
  component: lazyRouteComponent(() => import('./ProductsPage'), 'ProductsPage'),
})

export const productNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/products/new',
  beforeLoad: redirectUnless({ module: 'Products' }, '/'),
  component: lazyRouteComponent(() => import('./ProductNewPage'), 'ProductNewPage'),
})

interface ProductDetailSearch {
  // Open like the stack page's: any module may contribute a product-detail tab, so this stays
  // `string` rather than a closed union of the two this module happens to own.
  tab?: string
}

export const productDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/products/$id',
  beforeLoad: redirectUnless({ module: 'Products' }, '/'),
  component: lazyRouteComponent(() => import('./ProductDetailPage'), 'ProductDetailPage'),
  validateSearch: (search: Record<string, unknown>): ProductDetailSearch =>
    typeof search.tab === 'string' ? { tab: search.tab } : {},
})
