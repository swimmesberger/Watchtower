import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Boxes } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems, stackDetailTabs } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'
import { OverviewTab } from './OverviewTab'
import { SettingsTab } from './SettingsTab'

export const stacksManifest = defineModule({
  name: 'Stacks',
  when: { module: 'Stacks' },
  contributes: [
    contribute(sidebarItems, [
      { id: 'stacks', label: 'Stacks', icon: Boxes, to: '/stacks', order: 20 },
    ]),
    contribute(stackDetailTabs, [
      {
        id: 'overview',
        label: 'Overview',
        value: 'overview',
        order: 10,
        component: ({ stack, registerHistoryRow }) => (
          <OverviewTab stack={stack} registerHistoryRow={registerHistoryRow} />
        ),
      },
      {
        id: 'settings',
        label: 'Settings',
        value: 'settings',
        order: 40,
        component: ({ stack }) => <SettingsTab stack={stack} />,
      },
    ]),
  ],
})

interface StacksSearch {
  status?: 'ok' | 'failed'
}

export const stacksRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stacks',
  beforeLoad: redirectUnless({ module: 'Stacks' }, '/'),
  component: lazyRouteComponent(() => import('./StacksPage'), 'StacksPage'),
  validateSearch: (search: Record<string, unknown>): StacksSearch => {
    const status = search.status
    return status === 'ok' || status === 'failed' ? { status } : {}
  },
})

export const stackNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stacks/new',
  beforeLoad: redirectUnless({ module: 'Stacks' }, '/'),
  component: lazyRouteComponent(() => import('./StackNewPage'), 'StackNewPage'),
})

interface StackDetailSearch {
  // Tab values are open — any module may contribute a stack-detail tab — so this is `string`, not a
  // closed union of the tabs this module happens to know about.
  tab?: string
}

export const stackDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stacks/$id',
  beforeLoad: redirectUnless({ module: 'Stacks' }, '/'),
  component: lazyRouteComponent(() => import('./StackDetailPage'), 'StackDetailPage'),
  validateSearch: (search: Record<string, unknown>): StackDetailSearch =>
    typeof search.tab === 'string' ? { tab: search.tab } : {},
})
