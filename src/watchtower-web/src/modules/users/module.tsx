import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Users } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// Module AND role, per design.md §8. The axes are ANDed, so a signed-in non-administrator gets neither
// the sidebar entry nor the route even though the module itself is enabled. This is a UX projection —
// every users.* handler carries [RequireRole("Admin")], which is what actually refuses the call.
const USERS_GATE = { module: 'Users', role: 'Admin' } as const

export const usersManifest = defineModule({
  name: 'Users',
  when: USERS_GATE,
  contributes: [
    // Slotted between Credentials (50) and Settings (60): it belongs with the instance-wide
    // administration entries, not with the deployment ones above them.
    contribute(sidebarItems, [
      { id: 'users', label: 'Users', icon: Users, to: '/users', group: 'access', exact: true, order: 55 },
    ]),
  ],
})

export const usersRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/users',
  beforeLoad: redirectUnless(USERS_GATE, '/'),
  component: lazyRouteComponent(() => import('./UsersPage'), 'UsersPage'),
})
