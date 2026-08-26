import { useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getRouteApi, Link } from '@tanstack/react-router'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { Boxes, ChevronRight, Plus } from 'lucide-react'
import { productDetailTabs } from '@/platform/points'
import { api } from '@/lib/api'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { DeployLatestButton } from './DeployLatestButton'

const routeApi = getRouteApi('/products/$id')

export function ProductDetailPage() {
  const { id } = routeApi.useParams()
  const productId = Number(id)

  // Tabs come from the productDetailTabs extension point (already sorted by order), so the CI and
  // Releases tabs of later stages land here without this page knowing about them.
  const tabs = useContributions(productDetailTabs)

  const { tab } = routeApi.useSearch()
  const navigateTab = routeApi.useNavigate()
  const defaultTab = tabs[0]?.value ?? 'overview'
  const activeTab = tab ?? defaultTab
  const setTab = useCallback(
    (next: string) => {
      navigateTab({
        search: (prev) => ({ ...prev, tab: next === defaultTab ? undefined : next }),
        replace: true,
      })
    },
    [navigateTab, defaultTab],
  )

  const {
    data,
    isLoading,
    isError,
    error,
    refetch,
  } = useQuery({ queryKey: ['product', productId], queryFn: () => api.products.get(productId) })

  if (isLoading) return <ProductDetailSkeleton />

  if (isError || !data) {
    return (
      <Banner
        tone="danger"
        title="Couldn’t load this product"
        action={
          <Button variant="secondary" size="sm" onClick={() => refetch()}>
            Retry
          </Button>
        }
      >
        {(error as Error)?.message ?? 'The product may have been deleted.'}
      </Banner>
    )
  }

  const { product } = data
  const instanceCount = product.stackCount

  return (
    <div className="space-y-6">
      <nav aria-label="Breadcrumb" className="flex items-center gap-1 text-xs text-text-2">
        <Link
          to="/products"
          className="inline-flex items-center gap-1 rounded transition-colors hover:text-text focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
        >
          <ChevronRight className="size-3.5 rotate-180" aria-hidden />
          Products
        </Link>
        <span aria-hidden className="text-text-3">
          /
        </span>
        <span className="truncate font-medium text-text">{product.name}</span>
      </nav>

      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <h1 className="inline-flex items-center gap-2 truncate text-2xl font-semibold tracking-tight text-text">
            {product.name}
            <Badge tone={instanceCount > 0 ? 'brand' : 'neutral'}>
              <Boxes className="size-3" /> {instanceCount}
            </Badge>
          </h1>
          <p className="mt-1 truncate font-mono text-[12.5px] text-text-2">
            {product.repositoryUrl} · {product.defaultBranch} · {product.composeFilePath}
          </p>
        </div>
        {/* Exactly one state-dependent primary action (design.md §Product detail page). Nothing
            deploying it is a dead end without the first; with instances, the action a *product* can
            take is moving its fleet onto the newest release — which only exists in Releases mode. In
            Git mode there is still nothing a product-level button could do that the stack page does
            not already do better, so there is none. */}
        {instanceCount === 0 ? (
          <Button asChild variant="primary">
            <Link to="/stacks/new" search={{ productId: product.id }}>
              <Plus /> Create deployment
            </Link>
          </Button>
        ) : (
          product.releaseMode === 'releases' && <DeployLatestButton product={product} />
        )}
      </div>

      <Tabs value={activeTab} onValueChange={setTab}>
        <TabsList>
          {tabs.map((t) => (
            <TabsTrigger key={t.id} value={t.value}>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>

        {tabs.map((t) => (
          <TabsContent key={t.id} value={t.value}>
            <t.component product={product} />
          </TabsContent>
        ))}
      </Tabs>
    </div>
  )
}

function ProductDetailSkeleton() {
  return (
    <div className="space-y-6">
      <Skeleton variant="line" className="w-40" />
      <Skeleton variant="line" className="h-8 w-64" />
      <Skeleton variant="rect" className="h-40 w-full" />
    </div>
  )
}
