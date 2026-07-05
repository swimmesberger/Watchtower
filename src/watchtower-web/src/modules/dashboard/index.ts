import type { AppModule } from '@/platform/app-module'
import { dashboardManifest, dashboardRoute } from './module'

const dashboardModule = {
  manifest: dashboardManifest,
  routes: [dashboardRoute],
} satisfies AppModule

export default dashboardModule
