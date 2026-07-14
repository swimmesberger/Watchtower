import type { AppModule } from '@/platform/app-module'
import { proxyManifest, routesRoute } from './module'

const proxyModule = {
  manifest: proxyManifest,
  routes: [routesRoute],
} satisfies AppModule

export default proxyModule
