import type { AppModule } from '@/platform/app-module'
import { templatesManifest, templatesRoute, templateNewRoute, templateDetailRoute } from './module'

const templatesModule = {
  manifest: templatesManifest,
  routes: [templatesRoute, templateNewRoute, templateDetailRoute],
} satisfies AppModule

export default templatesModule
