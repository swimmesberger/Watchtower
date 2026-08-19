import type { AppModule } from '@/platform/app-module'
import { backupsManifest } from './module'

const backupsModule = { manifest: backupsManifest, routes: [] } satisfies AppModule

export default backupsModule
