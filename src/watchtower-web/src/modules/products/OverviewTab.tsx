import { useQuery } from '@tanstack/react-query'
import { Link, useRouteContext } from '@tanstack/react-router'
import { Boxes, Hammer, Layers, Plus, Tag, Users } from 'lucide-react'
import { api } from '@/lib/api'
import type { Product, ProductDetail, ProductStack, ProductTemplate } from '@/lib/types'
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

/**
 * The **Next steps** card (design.md §"SaaS flow" step 2) — "three rows, three sentences, three
 * buttons", and the key teaching screen a freshly created product opens on.
 *
 * **It exists only in the fully-empty case and vanishes the moment any of the three is done**: no
 * deployments, no tenancy, no releases, no CI link. That is the whole discipline of it — a teaching card
 * that outstayed the lesson would be the "product detail ballooning" risk the Übersichtlichkeit audit
 * names, and every row's door is somewhere the reader can reach on their own afterwards.
 *
 * Each row is gated on its module as well: a button pointing at a tab that is not contributed would be
 * a door into a wall.
 */
function NextStepsCard({ product, onCi }: { product: Product; onCi: boolean }) {
  const { caps } = useRouteContext({ from: '__root__' })
  const rows = [
    {
      id: 'deploy',
      title: 'Deploy it once',
      description: 'One running copy of this product on this host — its containers, its environment.',
      action: (
        <Button asChild variant="primary">
          <Link to="/stacks/new" search={{ productId: product.id }}>
            <Plus /> Create deployment
          </Link>
        </Button>
      ),
      show: true,
    },
    {
      id: 'tenants',
      title: 'Run it for many tenants',
      description: 'One isolated copy per customer, each on its own subdomain.',
      action: (
        <Button asChild variant="secondary">
          <Link to="/products/$id" params={{ id: String(product.id) }} search={{ tab: 'instances' }}>
            <Users /> Set up tenancy
          </Link>
        </Button>
      ),
      show: caps.isModuleEnabled('Tenancy'),
    },
    {
      id: 'ci',
      title: 'Build it here',
      description: "Run this repo's GitHub Actions jobs on this host and publish releases.",
      action: (
        <Button asChild variant="secondary">
          <Link to="/products/$id" params={{ id: String(product.id) }} search={{ tab: 'ci' }}>
            <Hammer /> Enable CI
          </Link>
        </Button>
      ),
      show: caps.isModuleEnabled('Ci') && onCi,
    },
  ].filter((r) => r.show)

  return (
    <Card>
      <CardContent>
        <SectionHeader
          title="Next steps"
          description="Nothing runs this product yet. Pick whichever of these you came here for — you can do the others later."
        />
        <ul className="divide-y divide-border rounded-lg border border-border">
          {rows.map((row) => (
            <li
              key={row.id}
              className="flex flex-wrap items-center justify-between gap-3 px-3 py-3.5"
            >
              <div className="min-w-0">
                <p className="text-sm font-medium text-text">{row.title}</p>
                <p className="mt-0.5 text-[13px] text-text-2">{row.description}</p>
              </div>
              <div className="shrink-0">{row.action}</div>
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
    <OverviewBody product={product} detail={data} stacks={stacks} templates={templates} />
  )
}

/**
 * Split out from {@link OverviewTab} so the CI probe below can be a hook: the tab returns early while
 * the product query is in flight, and a hook after an early return is a hook-order violation.
 */
function OverviewBody({
  product,
  detail,
  stacks,
  templates,
}: {
  product: Product
  detail: ProductDetail
  stacks: ProductStack[]
  templates: ProductTemplate[]
}) {
  const { caps } = useRouteContext({ from: '__root__' })
  // The three cheap halves of "fully empty", straight off the product query.
  const nothingDeploys = stacks.length === 0 && templates.length === 0 && product.latestRelease == null

  // The fourth half needs the CI link, so it is only asked for once the other three hold — a product
  // with instances never pays for this. Same query key as the CI tab and the Releases tab, so the three
  // share one cache entry.
  const ci = useQuery({
    queryKey: ['product', product.id, 'ci'],
    queryFn: () => api.ci.getProductCi(product.id),
    enabled: nothingDeploys && caps.isModuleEnabled('Ci'),
  })
  // A repo link is what "CI is set up here" means. While the probe is in flight the card waits rather
  // than flashing: a teaching screen that appears and then loses a row reads as a glitch.
  const ciSettled = !caps.isModuleEnabled('Ci') || ci.isSuccess || ci.isError
  const noCi = ci.data?.repo == null
  const showNextSteps = nothingDeploys && ciSettled && noCi

  return (
    <div className="space-y-6">
      {/* "latest ≠ branch head" (design.md §"Update checks and drift"). The first-release transition
          makes this routine — CI starts before the last push, so release #1 is often for commit N−1 —
          and a re-run of an old workflow can produce it at any time. Announced rather than
          special-cased, and it clears itself on the next release.

          Deliberately no count of commits: knowing "2 commits on main since v1" needs a clone, and this
          page must not make one. The two shas are what is actually known. */}
      {detail.unreleasedCommitSha && product.latestRelease && (
        <Banner tone="info" title={`${product.defaultBranch} has moved past ${product.latestRelease.version}`}>
          The branch head is{' '}
          <span className="font-mono">{detail.unreleasedCommitSha.slice(0, 7)}</span>
          {product.latestRelease.commitSha && (
            <>
              , and the latest release was built from{' '}
              <span className="font-mono">{product.latestRelease.commitSha.slice(0, 7)}</span>
            </>
          )}
          . Deployments run the release, not the branch — the next release picks the new commits up.
        </Banner>
      )}

      {/* The teaching screen leads, and it *replaces* the Deployments empty state rather than sitting
          under it: the two would otherwise put two "Create deployment" buttons on one screen, one of
          them inside a card explaining that there is nothing to list. */}
      {showNextSteps && <NextStepsCard product={product} onCi={noCi} />}

      {!showNextSteps && (
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
      )}

      <RecentReleasesCard product={product} />

      {/* Rendered only when tenancy is actually in play — an empty card teaching templates would be
          noise on the hobby path. */}
      {templates.length > 0 && (
        <Card>
          <CardContent>
            <SectionHeader
              title="Tenancy"
              description="Setups that run this product once per tenant, each on its own subdomain."
            />
            <ul className="divide-y divide-border rounded-lg border border-border">
              {templates.map((t) => (
                <li
                  key={t.id}
                  className="flex flex-wrap items-center justify-between gap-3 px-3 py-2.5"
                >
                  {/* The Instances tab of this very page owns it now (ADR-0026 stage 8b) — the summary
                      stays because Overview answers "what is this product", and the link is one hop
                      rather than a page. */}
                  <Link
                    to="/products/$id"
                    params={{ id: String(product.id) }}
                    search={{ tab: 'instances' }}
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
