// The shape each feature module default-exports from its `index.ts`. Manifests are DISCOVERED by glob in
// the composition root (a new contribution or a UI-only module needs no central edit); `routes` are
// REGISTERED there statically, one line per module, so TanStack keeps typing navigation (Elarion #71).
// A module's `index.ts` must `satisfies AppModule` — not annotate `: AppModule` — so its concrete route
// tuple survives instead of widening to `AnyRoute[]`. UI-only modules own no routes and pass `routes: []`.
import type { AnyRoute } from '@tanstack/react-router'
import type { ModuleManifest } from '@swimmesberger/elarion-contributions'
import type { AppVocabulary } from './vocabulary'

export interface AppModule {
  readonly manifest: ModuleManifest<AppVocabulary>
  readonly routes: readonly AnyRoute[]
}
