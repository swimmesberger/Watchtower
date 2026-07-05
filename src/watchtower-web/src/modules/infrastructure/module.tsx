import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Network } from 'lucide-react'
import { defineModule, contribute } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

// UI-only aggregator: the Infrastructure page hosts fleet-wide sections contributed by the volumes and
// networks modules. Desktop-only in the nav (mobile: false) — reachable on phones via a dashboard link.
export const infrastructureManifest = defineModule({
  name: 'infrastructure',
  contributes: [
    contribute(sidebarItems, [
      {
        id: 'infrastructure',
        label: 'Infrastructure',
        icon: Network,
        to: '/infrastructure',
        exact: true,
        order: 30,
        mobile: false,
      },
    ]),
  ],
})

export const infrastructureRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/infrastructure',
  component: lazyRouteComponent(() => import('./InfrastructurePage'), 'InfrastructurePage'),
})
