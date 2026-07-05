import type { AppModule } from '@/platform/app-module'
import { stacksManifest, stacksRoute, stackNewRoute, stackDetailRoute } from './module'

const stacksModule: AppModule = {
  manifest: stacksManifest,
  routes: [stacksRoute, stackNewRoute, stackDetailRoute],
}

export default stacksModule
