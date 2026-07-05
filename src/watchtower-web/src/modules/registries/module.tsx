import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Container } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

export const registriesManifest = defineModule({
  name: 'Registries',
  when: { module: 'Registries' },
  contributes: [
    contribute(sidebarItems, [
      { id: 'registries', label: 'Registries', icon: Container, to: '/registries', exact: true, order: 40 },
    ]),
  ],
})

export const registriesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/registries',
  beforeLoad: redirectUnless({ module: 'Registries' }, '/'),
  component: lazyRouteComponent(() => import('./RegistriesPage'), 'RegistriesPage'),
})
