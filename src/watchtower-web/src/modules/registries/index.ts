import type { AppModule } from '@/platform/app-module'
import { registriesManifest, registriesRoute } from './module'

const registriesModule = {
  manifest: registriesManifest,
  routes: [registriesRoute],
} satisfies AppModule

export default registriesModule
