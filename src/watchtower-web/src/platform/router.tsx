// The composition root. Manifests are DISCOVERED — the import.meta.glob below feeds every module's
// contributions to the registry, so a new contribution, sidebar item, or UI-only module needs no edit
// here. Routes are REGISTERED — one typed line per route-owning module in `addChildren`, the same grain
// as a backend host adding a ProjectReference — because a glob-composed route tree types as `AnyRoute[]`,
// which silently degrades `Link to`, `useParams`, and `useSearch` to untyped fallbacks app-wide (Elarion
// #71). UI-only modules (metrics, networks, volumes) own no routes and are discovered by glob only.
import { createRouter } from '@tanstack/react-router'
import { createContributionRegistry } from '@swimmesberger/elarion-contributions'
import { rootRoute } from './root-route'
import { capabilities } from './capabilities'
import type { AppModule } from './app-module'
import credentials from '@/modules/credentials'
import dashboard from '@/modules/dashboard'
import infrastructure from '@/modules/infrastructure'
import registries from '@/modules/registries'
import settings from '@/modules/settings'
import stacks from '@/modules/stacks'

// Vite expands the glob at build time into static imports, so manifest discovery stays compile-time,
// bundled, and deterministic (keys come back sorted). Used only for `.manifest` — routes come from the
// typed static imports above.
const discovered = import.meta.glob<AppModule>('../modules/*/index.ts', {
  eager: true,
  import: 'default',
})
const appModules = Object.values(discovered)

/** The resolved contribution registry, provided to the tree via `ContributionProvider` in the entry. */
export const registry = createContributionRegistry(
  appModules.map((m) => m.manifest),
  capabilities,
)

// Each `satisfies AppModule` module keeps its concrete route tuple, so the tree is statically typed and
// TanStack infers `Link`/`params`/`search` across the app.
const routeTree = rootRoute.addChildren([
  ...credentials.routes,
  ...dashboard.routes,
  ...infrastructure.routes,
  ...registries.routes,
  ...settings.routes,
  ...stacks.routes,
])

export const router = createRouter({
  routeTree,
  context: { queryClient: undefined!, caps: capabilities },
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
