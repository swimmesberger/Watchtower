// The shape each feature module default-exports from its `index.ts`. The composition root discovers
// these by glob and needs nothing else: the manifest carries the module's contributions (nav, tabs,
// sections…) and `routes` carries the TanStack route subtrees it owns. UI-only modules omit `routes`.
import type { AnyRoute } from '@tanstack/react-router'
import type { ModuleManifest } from '@swimmesberger/elarion-contributions'
import type { AppVocabulary } from './vocabulary'

export interface AppModule {
  readonly manifest: ModuleManifest<AppVocabulary>
  readonly routes?: readonly AnyRoute[]
}
