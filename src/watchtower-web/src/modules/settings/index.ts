import type { AppModule } from '@/platform/app-module'
import { settingsManifest, settingsRoute } from './module'

const settingsModule = {
  manifest: settingsManifest,
  routes: [settingsRoute],
} satisfies AppModule

export default settingsModule
