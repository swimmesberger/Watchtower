import type { AppModule } from '@/platform/app-module'
import { dashboardManifest, dashboardRoute } from './module'

const dashboardModule: AppModule = {
  manifest: dashboardManifest,
  routes: [dashboardRoute],
}

export default dashboardModule
