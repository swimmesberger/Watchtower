import type { AppModule } from '@/platform/app-module'
import { metricsManifest, metricsHistoryRoute } from './module'

const metricsModule = {
  manifest: metricsManifest,
  routes: [metricsHistoryRoute],
} satisfies AppModule

export default metricsModule
