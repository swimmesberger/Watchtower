import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { ScrollText } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// Module AND role, the same pair Users and Groups use. The trail names accounts and apps across every
// realm, so reading it is as privileged as administering them. A UX projection only — both audit.*
// handlers carry [RequireRole("Admin")], which is what actually refuses the call.
const AUDIT_GATE = { module: 'Audit', role: 'Admin' } as const

export const auditManifest = defineModule({
  name: 'Audit',
  when: AUDIT_GATE,
  contributes: [
    // After Users (55), Groups (56) and Realms (57), before Settings (60): the trail is the record of
    // what was done to the three entries above it, so it reads as the last of the access-control group
    // rather than the first of the instance-configuration one. Its own number rather than a tie on 57 —
    // equal orders fall back to an alphabetical tie-break, which would slot Audit between Groups and
    // Realms and split the three screens that are meant to be read together.
    contribute(sidebarItems, [
      { id: 'audit', label: 'Audit', icon: ScrollText, to: '/audit', exact: true, order: 58 },
    ]),
  ],
})

export const auditRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/audit',
  beforeLoad: redirectUnless(AUDIT_GATE, '/'),
  component: lazyRouteComponent(() => import('./AuditPage'), 'AuditPage'),
})
