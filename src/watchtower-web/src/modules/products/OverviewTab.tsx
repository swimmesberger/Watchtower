import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Boxes, Layers, Plus, Tag, Users } from 'lucide-react'
import { api } from '@/lib/api'
import type { Product, ProductStack } from '@/lib/types'
import { absoluteTitle, timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { SectionHeader } from '@/components/ui/section-header'
import { Spinner } from '@/components/ui/spinner'
import { StatusBadge } from '@/components/ui/status-badge'

/**
 * The last three builds, with the way through to the rest. Rendered only once a release exists: the
 * concept is taught in exactly one place (the Releases tab's empty state), and an empty card teaching
 * it a second time on the page a hobby user opens first is the noise ADR-0026's UX audit is about.
 */
function RecentReleasesCard({ product }: { product: Product }) {
  const { data } = useQuery({
    queryKey: ['product', product.id, 'releases', 'recent'],
    queryFn: () => api.products.listReleases(product.id, undefined, 3),
  })
  const releases = data?.releases ?? []
  if (releases.length === 0) return null

  return (
    <Card>
      <CardContent>
        <SectionHeader
          title="Recent releases"
          description="What this product's CI has built. Nothing deploys until you say so."
          action={
            <Button asChild variant="link">
              <Link to="/products/$id" params={{ id: String(product.id) }} search={{ tab: 'releases' }}>
                View all
              </Link>
            </Button>
          }
        />
        <ul className="divide-y divide-border rounded-lg border border-border">
          {releases.map((release, index) => (
            <li key={release.id} className="flex flex-wrap items-center justify-between gap-3 px-3 py-2.5">
              <div className="flex min-w-0 items-center gap-2">
                <Tag className="size-4 shrink-0 text-text-3" aria-hidden />
                <span className="truncate font-medium text-text">{release.version}</span>
                {index === 0 && (
                  <Badge tone="brand" size="sm">
                    latest
                  </Badge>
                )}
              </div>
              <div className="flex shrink-0 items-center gap-3 text-[13px] text-text-3">
                {release.commitSha && (
                  <span className="font-mono text-text-2" title={release.commitSha}>
                    {release.commitSha.slice(0, 7)}
                  </span>
                )}
                <span className="tnum" title={absoluteTitle(release.createdAt)}>
                  {timeAgo(release.createdAt)}
                </span>
              </div>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

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
      {/* "latest ≠ branch head" (design.md §"Update checks and drift"). The first-release transition
          makes this routine — CI starts before the last push, so release #1 is often for commit N−1 —
          and a re-run of an old workflow can produce it at any time. Announced rather than
          special-cased, and it clears itself on the next release.

          Deliberately no count of commits: knowing "2 commits on main since v1" needs a clone, and this
          page must not make one. The two shas are what is actually known. */}
      {data.unreleasedCommitSha && product.latestRelease && (
        <Banner tone="info" title={`${product.defaultBranch} has moved past ${product.latestRelease.version}`}>
          The branch head is{' '}
          <span className="font-mono">{data.unreleasedCommitSha.slice(0, 7)}</span>
          {product.latestRelease.commitSha && (
            <>
              , and the latest release was built from{' '}
              <span className="font-mono">{product.latestRelease.commitSha.slice(0, 7)}</span>
            </>
          )}
          . Deployments run the release, not the branch — the next release picks the new commits up.
        </Banner>
      )}

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

      <RecentReleasesCard product={product} />

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
