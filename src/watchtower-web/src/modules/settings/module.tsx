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
        group: 'system',
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

/**
 * Restoring this Watchtower from a full backup bundle (ADR-0027). Its own route rather than a card:
 * it is a multi-step flow that ends by taking the instance down, and it has to survive being the only
 * thing an operator does on a freshly installed box.
 */
export const restoreInstanceRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/settings/restore',
  beforeLoad: redirectUnless({ module: 'System' }, '/'),
  component: lazyRouteComponent(() => import('./RestoreInstancePage'), 'RestoreInstancePage'),
})
