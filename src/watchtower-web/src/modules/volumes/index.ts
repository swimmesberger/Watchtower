import type { AppModule } from '@/platform/app-module'
import { volumesManifest } from './module'

const volumesModule = { manifest: volumesManifest, routes: [] } satisfies AppModule

export default volumesModule
