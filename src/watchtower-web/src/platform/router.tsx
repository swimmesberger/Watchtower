// The composition root. Manifests are DISCOVERED — the import.meta.glob below feeds every module's
// contributions to the registry, so a new contribution, sidebar item, or UI-only module needs no edit
// here. Routes are REGISTERED — one typed line per route-owning module in `addChildren`, the same grain
// as a backend host adding a ProjectReference — because a glob-composed route tree types as `AnyRoute[]`,
// which silently degrades `Link to`, `useParams`, and `useSearch` to untyped fallbacks app-wide (Elarion
// #71). UI-only modules (networks, volumes) own no routes and are discovered by glob only.
import { createRouter } from '@tanstack/react-router'
import { rootRoute } from './root-route'
import { loginRoute } from './login-route'
import type { AppModule } from './app-module'
import audit from '@/modules/audit'
import credentials from '@/modules/credentials'
import dashboard from '@/modules/dashboard'
import groups from '@/modules/groups'
import infrastructure from '@/modules/infrastructure'
import metrics from '@/modules/metrics'
import proxy from '@/modules/proxy'
import realms from '@/modules/realms'
import registries from '@/modules/registries'
import settings from '@/modules/settings'
import stacks from '@/modules/stacks'
import templates from '@/modules/templates'
import users from '@/modules/users'

// Vite expands the glob at build time into static imports, so manifest discovery stays compile-time,
// bundled, and deterministic (keys come back sorted). Used only for `.manifest` — routes come from the
// typed static imports above.
const discovered = import.meta.glob<AppModule>('../modules/*/index.ts', {
  eager: true,
  import: 'default',
})
const appModules = Object.values(discovered)

/**
 * Every discovered module manifest. The entry (`main.tsx`) resolves them into the contribution registry
 * against the boot capability snapshot (ADR-0030) — one snapshot per boot gates contributions and routes
 * alike; refreshing means fetching again and rebuilding the registry.
 */
export const appManifests = appModules.map((m) => m.manifest)

// Each `satisfies AppModule` module keeps its concrete route tuple, so the tree is statically typed and
// TanStack infers `Link`/`params`/`search` across the app.
const routeTree = rootRoute.addChildren([
  // Platform-owned and deliberately ungated: it is the page an unauthenticated visitor is sent to.
  loginRoute,
  ...audit.routes,
  ...credentials.routes,
  ...dashboard.routes,
  ...groups.routes,
  ...infrastructure.routes,
  ...metrics.routes,
  ...proxy.routes,
  ...realms.routes,
  ...registries.routes,
  ...settings.routes,
  ...stacks.routes,
  ...templates.routes,
  ...users.routes,
])

// Context values are supplied at render time by `RouterProvider` in the entry (after the capability
// snapshot is fetched) — the `undefined!` placeholders here only satisfy the type at construction.
export const router = createRouter({
  routeTree,
  context: { queryClient: undefined!, caps: undefined! },
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
