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
// context) alike. Refreshing after a context change means fetching again and rebuilding both.
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
