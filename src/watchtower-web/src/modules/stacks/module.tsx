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
        component: ({ stack }) => <OverviewTab stack={stack} />,
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

type StackDetailTabValue = 'overview' | 'volumes' | 'networks' | 'settings'

interface StackDetailSearch {
  tab?: StackDetailTabValue
}

export const stackDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stacks/$id',
  beforeLoad: redirectUnless({ module: 'Stacks' }, '/'),
  component: lazyRouteComponent(() => import('./StackDetailPage'), 'StackDetailPage'),
  validateSearch: (search: Record<string, unknown>): StackDetailSearch => {
    const tab = search.tab
    return tab === 'overview' || tab === 'volumes' || tab === 'networks' || tab === 'settings'
      ? { tab }
      : {}
  },
})
