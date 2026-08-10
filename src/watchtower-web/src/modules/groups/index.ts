import type { AppModule } from '@/platform/app-module'
import { groupsManifest, groupsRoute } from './module'

const groupsModule = {
  manifest: groupsManifest,
  routes: [groupsRoute],
} satisfies AppModule

export default groupsModule
