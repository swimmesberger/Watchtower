import { createRoute, lazyRouteComponent } from '@tanstack/react-router'
import { Layers } from 'lucide-react'
import { defineModule, contribute, redirectUnless } from '@/platform/contributions'
import { sidebarItems } from '@/platform/points'
import { rootRoute } from '@/platform/root-route'

export const templatesManifest = defineModule({
  name: 'Tenancy',
  when: { module: 'Tenancy' },
  contributes: [
    contribute(sidebarItems, [
      { id: 'templates', label: 'Templates', icon: Layers, to: '/templates', order: 22 },
    ]),
  ],
})

export const templatesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/templates',
  beforeLoad: redirectUnless({ module: 'Tenancy' }, '/'),
  component: lazyRouteComponent(() => import('./TemplatesPage'), 'TemplatesPage'),
})

export const templateNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/templates/new',
  beforeLoad: redirectUnless({ module: 'Tenancy' }, '/'),
  component: lazyRouteComponent(() => import('./TemplateNewPage'), 'TemplateNewPage'),
})

export const templateDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/templates/$id',
  beforeLoad: redirectUnless({ module: 'Tenancy' }, '/'),
  component: lazyRouteComponent(() => import('./TemplateDetailPage'), 'TemplateDetailPage'),
})
