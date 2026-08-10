import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { UsersRound } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// Module AND role, the same pair the Users module uses and for the same reason: putting an account into a
// group grants it every route that group is named on, so this screen is exactly as privileged as the one
// that writes grants. A UX projection only — every groups.* handler carries [RequireRole("Admin")].
const GROUPS_GATE = { module: 'Groups', role: 'Admin' } as const

export const groupsManifest = defineModule({
  name: 'Groups',
  when: GROUPS_GATE,
  contributes: [
    // Immediately after Users (55): the two are read together — a group is only a set of those accounts.
    contribute(sidebarItems, [
      { id: 'groups', label: 'Groups', icon: UsersRound, to: '/groups', exact: true, order: 56 },
    ]),
  ],
})

export const groupsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/groups',
  beforeLoad: redirectUnless(GROUPS_GATE, '/'),
  component: lazyRouteComponent(() => import('./GroupsPage'), 'GroupsPage'),
})
