import type { AppModule } from '@/platform/app-module'
import { metricsManifest } from './module'

const metricsModule = {
  manifest: metricsManifest,
  routes: [],
} satisfies AppModule

export default metricsModule
