import type { AppModule } from '@/platform/app-module'
import { metricsManifest } from './module'

const metricsModule: AppModule = {
  manifest: metricsManifest,
}

export default metricsModule
