import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from '@tanstack/react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ContributionProvider } from '@swimmesberger/elarion-contributions/react'
import { router, registry } from './platform/router'
import { capabilities } from './platform/capabilities'
import './styles.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 15_000, retry: 1 } },
})

const rootEl = document.getElementById('root')
if (!rootEl) throw new Error('Root element not found')

createRoot(rootEl).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ContributionProvider registry={registry}>
        <RouterProvider router={router} context={{ queryClient, caps: capabilities }} />
      </ContributionProvider>
    </QueryClientProvider>
  </StrictMode>,
)
