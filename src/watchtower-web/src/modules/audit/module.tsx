import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { ScrollText } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// Module AND role, like Users: the trail names hostnames, tunnels and raw error messages, so it is
// an administrator's view. UX projection only — audit.listEvents carries [RequireRole("Admin")].
const AUDIT_GATE = { module: 'Audit', role: 'Admin' } as const

export const auditManifest = defineModule({
  name: 'Audit',
  when: AUDIT_GATE,
  contributes: [
    // With the instance-administration entries: after Users/Groups/Realms (55–57), before Settings (60).
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
