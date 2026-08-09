import type { AppModule } from '@/platform/app-module'
import { usersManifest, usersRoute } from './module'

const usersModule = {
  manifest: usersManifest,
  routes: [usersRoute],
} satisfies AppModule

export default usersModule
