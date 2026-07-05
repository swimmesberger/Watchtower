import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Settings } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'
import { SettingsUpdateBadge } from './SettingsUpdateBadge'

export const settingsManifest = defineModule({
  name: 'System',
  when: { module: 'System' },
  contributes: [
    contribute(sidebarItems, [
      {
        id: 'settings',
        label: 'Settings',
        icon: Settings,
        to: '/settings',
        exact: true,
        order: 60,
        badge: SettingsUpdateBadge,
      },
    ]),
  ],
})

export const settingsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/settings',
  beforeLoad: redirectUnless({ module: 'System' }, '/'),
  component: lazyRouteComponent(() => import('./SettingsPage'), 'SettingsPage'),
})
