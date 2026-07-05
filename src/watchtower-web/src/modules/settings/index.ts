import type { AppModule } from '@/platform/app-module'
import { settingsManifest, settingsRoute } from './module'

const settingsModule: AppModule = {
  manifest: settingsManifest,
  routes: [settingsRoute],
}

export default settingsModule
