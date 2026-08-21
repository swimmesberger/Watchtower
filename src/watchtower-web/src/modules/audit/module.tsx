import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { ScrollText } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// Module AND role, the same pair Users and Groups use. The trails name accounts, apps, hostnames,
// tunnels and raw error messages across every realm, so reading them is as privileged as
// administering them. A UX projection only — every audit.* handler carries [RequireRole("Admin")],
// which is what actually refuses the call.
const AUDIT_GATE = { module: 'Audit', role: 'Admin' } as const

export const auditManifest = defineModule({
  name: 'Audit',
  when: AUDIT_GATE,
  contributes: [
    // In the System group, after the access-control entries: the trail is the record of what was
    // done to the screens above it.
    contribute(sidebarItems, [
      { id: 'audit', label: 'Audit', icon: ScrollText, to: '/audit', group: 'system', exact: true, order: 58 },
    ]),
  ],
})

export const auditRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/audit',
  beforeLoad: redirectUnless(AUDIT_GATE, '/'),
  component: lazyRouteComponent(() => import('./AuditPage'), 'AuditPage'),
})
