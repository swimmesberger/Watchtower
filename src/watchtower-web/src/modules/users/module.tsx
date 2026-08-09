import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Users } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

export const usersManifest = defineModule({
  name: 'Users',
  when: { module: 'Users' },
  contributes: [
    // Slotted between Credentials (50) and Settings (60): it belongs with the instance-wide
    // administration entries, not with the deployment ones above them.
    contribute(sidebarItems, [
      { id: 'users', label: 'Users', icon: Users, to: '/users', exact: true, order: 55 },
    ]),
  ],
})

export const usersRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/users',
  beforeLoad: redirectUnless({ module: 'Users' }, '/'),
  component: lazyRouteComponent(() => import('./UsersPage'), 'UsersPage'),
})
