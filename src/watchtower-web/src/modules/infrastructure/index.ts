import type { AppModule } from '@/platform/app-module'
import { infrastructureManifest, infrastructureRoute } from './module'

const infrastructureModule: AppModule = {
  manifest: infrastructureManifest,
  routes: [infrastructureRoute],
}

export default infrastructureModule
