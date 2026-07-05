import type { AppModule } from '@/platform/app-module'
import { infrastructureManifest, infrastructureRoute } from './module'

const infrastructureModule = {
  manifest: infrastructureManifest,
  routes: [infrastructureRoute],
} satisfies AppModule

export default infrastructureModule
