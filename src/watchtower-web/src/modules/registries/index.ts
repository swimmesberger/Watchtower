import type { AppModule } from '@/platform/app-module'
import { registriesManifest, registriesRoute } from './module'

const registriesModule: AppModule = {
  manifest: registriesManifest,
  routes: [registriesRoute],
}

export default registriesModule
