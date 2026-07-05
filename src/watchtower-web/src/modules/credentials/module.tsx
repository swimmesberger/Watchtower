import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Key } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

export const credentialsManifest = defineModule({
  name: 'Credentials',
  when: { module: 'Credentials' },
  contributes: [
    contribute(sidebarItems, [
      { id: 'credentials', label: 'Credentials', icon: Key, to: '/credentials', exact: true, order: 50 },
    ]),
  ],
})

export const credentialsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/credentials',
  beforeLoad: redirectUnless({ module: 'Credentials' }, '/'),
  component: lazyRouteComponent(() => import('./CredentialsPage'), 'CredentialsPage'),
})
