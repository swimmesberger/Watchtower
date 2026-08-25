import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Boxes, Layers, Plus, Users } from 'lucide-react'
import { api } from '@/lib/api'
import type { Product, ProductStack } from '@/lib/types'
import { absoluteTitle, timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { SectionHeader } from '@/components/ui/section-header'
import { Spinner } from '@/components/ui/spinner'
import { StatusBadge } from '@/components/ui/status-badge'

/** Only shown when the stack deploys something other than the branch it would inherit. */
function BranchNote({ stack }: { stack: ProductStack }) {
  if (!stack.branchOverride) return null
  return (
    <Badge tone="neutral" size="sm">
      {stack.branchOverride}
    </Badge>
  )
}

export function OverviewTab({ product }: { product: Product }) {
  // Served from the cache the detail page primed; the query key is shared so a settings save
  // refreshes both.
  const { data, isLoading } = useQuery({
    queryKey: ['product', product.id],
    queryFn: () => api.products.get(product.id),
  })

  if (isLoading || !data) {
    return (
      <div className="flex justify-center p-10">
        <Spinner />
      </div>
    )
  }

  const { stacks, templates } = data

  return (
    <div className="space-y-6">
      <Card>
        <CardContent>
          <SectionHeader
            title="Deployments"
            description="The running copies of this product. Their containers, domains and history live on each stack."
          />
          {stacks.length === 0 ? (
            <EmptyState
              icon={Boxes}
              title="Nothing deploys this product yet"
              description="A stack is one running copy of a product — its containers, its environment, its history."
              action={
                <Button asChild variant="primary">
                  <Link to="/stacks/new" search={{ productId: product.id }}>
                    <Plus /> Create deployment
                  </Link>
                </Button>
              }
            />
          ) : (
            <ul className="divide-y divide-border rounded-lg border border-border">
              {stacks.map((s) => (
                <li
                  key={s.id}
                  className="flex flex-wrap items-center justify-between gap-3 px-3 py-2.5"
                >
                  <div className="flex min-w-0 items-center gap-2">
                    <Link
                      to="/stacks/$id"
                      params={{ id: String(s.id) }}
                      className="truncate font-medium text-text hover:text-brand"
                    >
                      {s.name}
                    </Link>
                    {s.tenantSlug && (
                      <Badge tone="neutral" size="sm">
                        tenant
                      </Badge>
                    )}
                    <BranchNote stack={s} />
                  </div>
                  <div className="flex shrink-0 items-center gap-3">
                    <StatusBadge status={s.lastDeployStatus} />
                    {/* The badge already says "never deployed" when there is no timestamp. */}
                    {s.lastDeployedAt && (
                      <span
                        className="tnum text-[13px] text-text-3"
                        title={absoluteTitle(s.lastDeployedAt)}
                      >
                        {timeAgo(s.lastDeployedAt)}
                      </span>
                    )}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>

      {/* Rendered only when tenancy is actually in play — an empty card teaching templates would be
          noise on the hobby path. */}
      {templates.length > 0 && (
        <Card>
          <CardContent>
            <SectionHeader
              title="Tenancy"
              description="Templates that run this product once per tenant, each on its own subdomain."
            />
            <ul className="divide-y divide-border rounded-lg border border-border">
              {templates.map((t) => (
                <li
                  key={t.id}
                  className="flex flex-wrap items-center justify-between gap-3 px-3 py-2.5"
                >
                  <Link
                    to="/templates/$id"
                    params={{ id: String(t.id) }}
                    className="inline-flex min-w-0 items-center gap-2 font-medium text-text hover:text-brand"
                  >
                    <Layers className="size-4 shrink-0 text-text-3" />
                    <span className="truncate">{t.name}</span>
                  </Link>
                  <div className="flex shrink-0 items-center gap-2">
                    {t.branchOverride && (
                      <Badge tone="neutral" size="sm">
                        {t.branchOverride}
                      </Badge>
                    )}
                    <Badge tone={t.tenantCount > 0 ? 'brand' : 'neutral'}>
                      <Users className="size-3" /> {t.tenantCount}
                    </Badge>
                  </div>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
