import type { AppModule } from '@/platform/app-module'
import { networksManifest } from './module'

const networksModule = {
  manifest: networksManifest,
  routes: [],
} satisfies AppModule

export default networksModule
