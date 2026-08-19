import type { AppModule } from '@/platform/app-module'
import { auditManifest, auditRoute } from './module'

const auditModule = {
  manifest: auditManifest,
  routes: [auditRoute],
} satisfies AppModule

export default auditModule
