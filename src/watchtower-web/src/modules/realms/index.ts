import type { AppModule } from '@/platform/app-module'
import { realmsManifest, realmsRoute } from './module'

const realmsModule = {
  manifest: realmsManifest,
  routes: [realmsRoute],
} satisfies AppModule

export default realmsModule
