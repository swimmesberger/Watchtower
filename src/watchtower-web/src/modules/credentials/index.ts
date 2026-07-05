import type { AppModule } from '@/platform/app-module'
import { credentialsManifest, credentialsRoute } from './module'

const credentialsModule: AppModule = {
  manifest: credentialsManifest,
  routes: [credentialsRoute],
}

export default credentialsModule
