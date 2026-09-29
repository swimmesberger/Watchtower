import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from '@tanstack/react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createContributionRegistry } from '@swimmesberger/elarion-contributions'
import { ContributionProvider } from '@swimmesberger/elarion-contributions/react'
import { router, appManifests } from './platform/router'
import { loadCapabilities } from './platform/capabilities'
import './styles.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 15_000, retry: 1 } },
})

// One capability snapshot per boot (ADR-0030) gates contributions (the registry) and routes (the router
// context) alike. Wrapped in an async bootstrap (not top-level await) so the production bundle stays within
// the configured browser target — refreshing after a context change means fetching again and rebuilding both.
async function bootstrap() {
  const caps = await loadCapabilities()
  const registry = createContributionRegistry(appManifests, caps)

  const rootEl = document.getElementById('root')
  if (!rootEl) throw new Error('Root element not found')

  createRoot(rootEl).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <ContributionProvider registry={registry}>
          <RouterProvider router={router} context={{ queryClient, caps }} />
        </ContributionProvider>
      </QueryClientProvider>
    </StrictMode>,
  )
}

void bootstrap()

// The service worker makes the home-screen install an app (offline shell) and receives Web Push. Production
// only: in the Vite dev server it would cache modules that hot reload is about to replace.
if ('serviceWorker' in navigator && !import.meta.env.DEV) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {
      // An installable shell is an enhancement; the app works without it.
    })
  })
}
