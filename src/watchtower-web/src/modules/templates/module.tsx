import { createRoute, lazyRouteComponent, redirect } from '@tanstack/react-router'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { productDetailTabs } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'
import { InstancesTab } from './InstancesTab'

// ── The Tenancy module after the fold (ADR-0026 stage 8b) ───────────────────────
//
// design.md §Navigation: **Templates leaves the sidebar.** A template was always "a product plus
// tenancy rules", so it is now the product's tenancy setup on the product's Instances tab, and the
// three `/templates*` routes are redirects.
//
// The module keeps its name and its RPC wrappers — this is an IA move, not an API change. What it
// contributes changed: one `productDetailTabs` entry instead of one `sidebarItems` entry, ordered at 30
// so it lands between Releases (20) and Backups (35), which is the order design.md lists them in. The
// tab is contributed *here* rather than owned by the products module for the same reason CI and Backups
// contribute theirs: modules never import each other, and Instances is tenancy's screen.
//
// **The fold makes Tenancy depend on Products, and that is stated in code rather than assumed.** Every
// surface this module now has is inside a product page, so with `Products` off there is no page for the
// tab to land on and no catalogue for the redirects to reach. The contribution and both redirects
// therefore carry a `Products` condition of their own — a contribution's `when` is ANDed with the
// manifest's, which is how the kernel expresses "both modules" — so the tab is *absent* rather than
// registered-and-unreachable, and `/templates*` goes straight Home instead of bouncing off `/products`
// on its way there. The reachability consequence (Tenancy needs Products to have any UI at all) is
// recorded as accepted debt in docs/products/implementation-status.md.

export const templatesManifest = defineModule({
  name: 'Tenancy',
  when: { module: 'Tenancy' },
  contributes: [
    contribute(productDetailTabs, [
      {
        id: 'instances',
        label: 'Instances',
        value: 'instances',
        order: 30,
        // ANDed with the manifest's `{ module: 'Tenancy' }` — the tab needs both.
        when: { module: 'Products' },
        component: ({ product }) => <InstancesTab product={product} />,
      },
    ]),
  ],
})

/** With Tenancy off, a `/templates*` deep link goes where it always did. */
const requireTenancy = redirectUnless({ module: 'Tenancy' }, '/')

/**
 * With Products off there is no catalogue to send anyone to, so the hop stops at Home in one step. Run
 * *after* {@link requireTenancy}: whichever module is missing, the answer is the same page, and the
 * order only decides which guard says so.
 */
const requireProducts = redirectUnless({ module: 'Products' }, '/')

/** `/templates` → the catalogue that replaced it. */
export const templatesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/templates',
  beforeLoad: (opts) => {
    requireTenancy(opts)
    requireProducts(opts)
    throw redirect({ to: '/products', replace: true })
  },
})

/**
 * `/templates/new` → the catalogue. Creating a tenancy setup starts from a *product* now (the form has
 * no source card left to fill in), and no id in this URL says which one.
 */
export const templateNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/templates/new',
  beforeLoad: (opts) => {
    requireTenancy(opts)
    requireProducts(opts)
    throw redirect({ to: '/products', replace: true })
  },
})

/**
 * `/templates/$id` → the product's Instances tab, which is where that template's screen went.
 *
 * The hop needs a lookup (the product id is on the template), so it is a **component** and not an async
 * `beforeLoad`: see the remarks on {@link TemplateRedirect} for why a guard that has to await is the
 * wrong shape here.
 */
export const templateDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/templates/$id',
  beforeLoad: (opts) => {
    requireTenancy(opts)
    requireProducts(opts)
  },
  component: lazyRouteComponent(() => import('./TemplateRedirect'), 'TemplateRedirect'),
})
