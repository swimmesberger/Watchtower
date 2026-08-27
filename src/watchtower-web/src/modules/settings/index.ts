import type { AppModule } from '@/platform/app-module'
import { restoreInstanceRoute, settingsManifest, settingsRoute } from './module'

const settingsModule = {
  manifest: settingsManifest,
  routes: [settingsRoute, restoreInstanceRoute],
} satisfies AppModule

export default settingsModule
