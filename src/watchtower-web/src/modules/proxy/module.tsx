import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Globe } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

export const proxyManifest = defineModule({
  name: 'Proxy',
  when: { module: 'Proxy' },
  contributes: [
    contribute(sidebarItems, [
      { id: 'routes', label: 'Routes', icon: Globe, to: '/routes', order: 25 },
    ]),
  ],
})

export const routesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/routes',
  beforeLoad: redirectUnless({ module: 'Proxy' }, '/'),
  component: lazyRouteComponent(() => import('./RoutesPage'), 'RoutesPage'),
})
