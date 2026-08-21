import type { AppModule } from '@/platform/app-module'
import { ciManifest } from './module'

const ciModule = { manifest: ciManifest, routes: [] } satisfies AppModule

export default ciModule
