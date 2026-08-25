import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { Boxes, Package, Plus } from 'lucide-react'
import { api } from '@/lib/api'
import type { Product } from '@/lib/types'
import { absoluteTitle, timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'

/** The catalogue reads better without the host everyone already knows. */
const repoLabel = (url: string) => url.replace(/^https:\/\/github\.com\//, '')

/**
 * Deliberately four columns and no status column: status belongs to the instances, and a fifth
 * column is what would turn this into a second Stacks page (design.md §Übersichtlichkeit audit).
 */
function InstanceBadges({ product }: { product: Product }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <Badge tone={product.stackCount > 0 ? 'brand' : 'neutral'}>
        <Boxes className="size-3" /> {product.stackCount}
      </Badge>
      {product.templateCount > 0 && (
        <Badge tone="neutral" size="sm">
          tenants
        </Badge>
      )}
    </span>
  )
}

export function ProductsPage() {
  const navigate = useNavigate()

  const {
    data: products = [],
    isLoading,
    isError,
    error,
    refetch,
  } = useQuery({ queryKey: ['products'], queryFn: api.products.list })

  const openProduct = (p: Product) =>
    navigate({ to: '/products/$id', params: { id: String(p.id) } })

  const columns: DataListColumn<Product>[] = [
    {
      key: 'name',
      header: 'Product',
      cell: (p) => (
        <Link
          to="/products/$id"
          params={{ id: String(p.id) }}
          className="inline-flex items-center gap-2 font-medium text-text hover:text-brand"
        >
          <Package className="size-4 text-text-3" />
          {p.name}
        </Link>
      ),
    },
    {
      key: 'repository',
      header: 'Repository',
      cell: (p) => (
        <span className="block max-w-[32ch] truncate font-mono text-[13px] text-text-2">
          {repoLabel(p.repositoryUrl)}
        </span>
      ),
    },
    {
      key: 'instances',
      header: 'Instances',
      cell: (p) => <InstanceBadges product={p} />,
    },
    {
      key: 'created',
      header: 'Created',
      cell: (p) => (
        <span className="tnum text-[13px] text-text-2" title={absoluteTitle(p.createdAt)}>
          {timeAgo(p.createdAt)}
        </span>
      ),
    },
  ]

  const renderCard = (p: Product) => (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3">
        <Link
          to="/products/$id"
          params={{ id: String(p.id) }}
          className="inline-flex items-center gap-2 font-medium text-text hover:text-brand"
        >
          <Package className="size-4 text-text-3" />
          {p.name}
        </Link>
        <InstanceBadges product={p} />
      </div>
      <p className="truncate font-mono text-[13px] text-text-2">{repoLabel(p.repositoryUrl)}</p>
    </div>
  )

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Products</h1>
        <Button asChild variant="primary">
          <Link to="/products/new">
            <Plus /> New product
          </Link>
        </Button>
      </div>

      {isError && (
        <Banner
          tone="danger"
          title="Couldn’t load products"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          {(error as Error)?.message ?? 'An unexpected error occurred.'}
        </Banner>
      )}

      {!isError && (
        <DataList
          items={products}
          getKey={(p) => p.id}
          columns={columns}
          renderCard={renderCard}
          onRowClick={openProduct}
          skeletonRows={isLoading ? 4 : undefined}
          emptyState={
            <EmptyState
              icon={Package}
              title="No products yet"
              // Stage 1 has no releases. Restore the design doc's wording ("its compose file, and
              // optionally the releases your CI builds") when stage 3 ships the Releases tab.
              description="A product is a git repository Watchtower deploys — its compose file and the stacks running it."
              action={
                // Stacked rather than side by side: the second line is an alternative route for a
                // different persona, not a secondary button competing with the primary one.
                <div className="flex flex-col items-center gap-3">
                  <Button asChild variant="primary">
                    <Link to="/products/new">
                      <Plus /> New product
                    </Link>
                  </Button>
                  <p className="text-[13px] text-text-2">
                    Just deploying one repo?{' '}
                    <Link to="/stacks/new" className="text-brand hover:underline">
                      Start with a stack
                    </Link>{' '}
                    — we’ll create the product for you.
                  </p>
                </div>
              }
            />
          }
          aria-label="Products"
        />
      )}
    </div>
  )
}
