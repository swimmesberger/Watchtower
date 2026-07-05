import type { AppModule } from '@/platform/app-module'
import { credentialsManifest, credentialsRoute } from './module'

const credentialsModule = {
  manifest: credentialsManifest,
  routes: [credentialsRoute],
} satisfies AppModule

export default credentialsModule
