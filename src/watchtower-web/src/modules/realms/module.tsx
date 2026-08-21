import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Building2 } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// Module AND role, like Users and Groups and for the stronger version of the same reason: a realm is a
// whole user population, so creating one decides which credentials a set of applications will ever accept.
// A UX projection only — every realms.* handler carries [RequireRole("Admin")], which is what refuses the
// call.
const REALMS_GATE = { module: 'Realms', role: 'Admin' } as const

export const realmsManifest = defineModule({
  name: 'Realms',
  when: REALMS_GATE,
  contributes: [
    // After Groups (56) and before Settings (60): Users, Groups and Realms are read together — a realm is
    // the population the other two are scoped to.
    contribute(sidebarItems, [
      { id: 'realms', label: 'Realms', icon: Building2, to: '/realms', group: 'access', exact: true, order: 57 },
    ]),
  ],
})

export const realmsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/realms',
  beforeLoad: redirectUnless(REALMS_GATE, '/'),
  component: lazyRouteComponent(() => import('./RealmsPage'), 'RealmsPage'),
})
