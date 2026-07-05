import type { AppModule } from '@/platform/app-module'
import { stacksManifest, stacksRoute, stackNewRoute, stackDetailRoute } from './module'

const stacksModule = {
  manifest: stacksManifest,
  routes: [stacksRoute, stackNewRoute, stackDetailRoute],
} satisfies AppModule

export default stacksModule
