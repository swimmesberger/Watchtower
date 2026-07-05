// The composition root. It discovers feature modules by glob — a new module is a new folder under
// `src/modules/`, with no edit here — builds the contribution registry from their manifests, and
// assembles the route tree from the routes they own. No central list of modules exists.
import { createRouter, type AnyRouter } from '@tanstack/react-router'
import { createContributionRegistry, type ModuleManifest } from '@swimmesberger/elarion-contributions'
import { rootRoute } from './root-route'
import { capabilities } from './capabilities'
import type { AppModule } from './app-module'

const discovered = import.meta.glob<AppModule>('../modules/*/index.ts', {
  eager: true,
  import: 'default',
})
const appModules = Object.values(discovered)

/** The resolved contribution registry, provided to the tree via `ContributionProvider` in the entry. */
export const registry = createContributionRegistry(
  appModules.map((m) => m.manifest as ModuleManifest),
  capabilities,
)

// Routes are composed dynamically from the discovered modules, so the tree can't be statically typed
// the way a hand-written tree is — TanStack's precise route-literal inference is traded for the
// no-central-edits module model. `to`/`params` on <Link> fall back to string typing.
const moduleRoutes = appModules.flatMap((m) => m.routes ?? [])
const routeTree = rootRoute.addChildren(moduleRoutes as never[])

// Typed as AnyRouter on purpose: with a runtime-composed tree there is no static route-literal union,
// so `<Link to>` / `useParams({ from })` fall back to string typing rather than erroring against an
// empty concrete tree. This is the deliberate trade for the no-central-edits module model.
export const router: AnyRouter = createRouter({
  routeTree,
  context: { queryClient: undefined!, caps: capabilities },
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
