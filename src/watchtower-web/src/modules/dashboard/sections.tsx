// The dashboard-owned sections. Each is self-contained: it runs its own queries and owns its own
// loading/empty/error states, so the DashboardPage host can render it blindly in contribution order.
// (The sibling metrics module contributes the host-health strip + resource-usage ranking separately.)
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link } from '@tanstack/react-router'
import {
  Boxes,
  Container as ContainerIcon,
  Package,
  Play,
  Plus,
  Users,
  XCircle,
} from 'lucide-react'
import { api } from '@/lib/api'
import { deployTargetVersion, usesReleases, versionRollup } from '@/lib/release'
import type { ActiveDeployment, Product, Stack } from '@/lib/types'
import { absoluteTitle, timeAgo, useElapsed } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { StatCard } from '@/components/ui/stat-card'
import { StatusBadge } from '@/components/ui/status-badge'
import { toast } from '@/components/ui/use-toast'

// The dashboard's own route — the only place these sections render, and where the boot capability
// snapshot hangs. Read for module gating (the Fleets section below), the same way `StacksPage` reads it.
const dashboardApi = getRouteApi('/')

// ── Self-update banner (order 5) ─────────────────────────────────────────────

/**
 * Renders the "update available" warn Banner (links to /settings) when Watchtower is outdated;
 * renders nothing otherwise. Self-contained: queries `system.getSelf`.
 */
export function UpdateBannerSection() {
  const { data: selfStatus } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: 5 * 60_000,
    retry: false,
  })

  if (!selfStatus?.isOutdated) return null

  return (
    <Banner
      tone="warn"
      title="Watchtower update available"
      dismissible
      action={
        <Button asChild variant="link" size="sm">
          <Link to="/settings">Review →</Link>
        </Button>
      }
    >
      A newer version of Watchtower has been detected.
    </Banner>
  )
}

// ── Summary stat cards (order 20) ────────────────────────────────────────────

/**
 * The 4 StatCards: Total stacks · Healthy · Failed · Containers (A5 links). Self-contained:
 * queries the stacks + containers lists. Shows a stat-card skeleton while stacks load.
 */
export function SummarySection() {
  const stacksQuery = useQuery({
    queryKey: ['stacks'],
    queryFn: api.stacks.list,
  })
  const stacks = stacksQuery.data ?? []

  const { data: containers = [] } = useQuery({
    queryKey: ['containers'],
    queryFn: api.containers.list,
  })

  if (stacksQuery.isLoading) return <SummarySkeleton />

  const healthy = stacks.filter((s) => s.lastDeployStatus === 'success').length
  const failed = stacks.filter((s) => s.lastDeployStatus === 'failed').length

  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
      <StatCard label="Total stacks" value={stacks.length} accent="brand" to="/stacks" />
      <StatCard
        label="Healthy"
        value={healthy}
        accent="ok"
        dotTone="ok"
        to="/stacks"
        search={{ status: 'ok' }}
      />
      <StatCard
        label="Failed"
        value={failed}
        accent="danger"
        dotTone="danger"
        to="/stacks"
        search={{ status: 'failed' }}
      />
      <StatCard
        label="Containers"
        value={containers.length}
        accent="neutral"
        icon={ContainerIcon}
      />
    </div>
  )
}

function SummarySkeleton() {
  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
      {Array.from({ length: 4 }).map((_, i) => (
        <Card key={i} className="p-4">
          <Skeleton variant="line" className="h-3 w-16" />
          <Skeleton variant="line" className="mt-3 h-7 w-10" />
        </Card>
      ))}
    </div>
  )
}

// ── Active deployments (order 30) ────────────────────────────────────────────

/**
 * The active-deployments panel; renders nothing when there are none. Self-contained: queries
 * `deployments.active` — A7 fast poll (2.5s) while non-empty, slow (10s) when idle.
 */
export function ActiveDeploymentsSection() {
  const activeDeploymentsQuery = useQuery({
    queryKey: ['deployments', 'active'],
    queryFn: api.deployments.active,
    // A7: fast poll (2.5s) while there are active deployments, slow (10s) when idle.
    refetchInterval: (query) =>
      (query.state.data?.length ?? 0) > 0 ? 2_500 : 10_000,
  })
  const activeDeployments = activeDeploymentsQuery.data ?? []

  if (activeDeployments.length === 0) return null

  return (
    <section>
      <SectionHeader title="Active deployments" action={<LiveChip />} />
      <Card>
        <ul className="divide-y divide-border">
          {activeDeployments.map((d) => (
            <ActiveDeploymentRow key={d.id} deployment={d} />
          ))}
        </ul>
      </Card>
    </section>
  )
}

function ActiveDeploymentRow({ deployment: d }: { deployment: ActiveDeployment }) {
  const elapsed = useElapsed(d.startedAt)
  return (
    <li className="flex items-center gap-3 p-4 md:px-5">
      <StatusBadge status={d.status} size="sm" />
      <div className="min-w-0 flex-1">
        <Link
          to="/stacks/$id"
          params={{ id: String(d.stackId) }}
          className="block truncate text-sm font-medium text-text transition-colors hover:text-brand"
        >
          {d.stackName}
        </Link>
        <p className="mt-0.5 truncate text-xs text-text-3">
          triggered by {d.triggeredBy}
        </p>
      </div>
      <span
        className="tnum shrink-0 font-mono text-[13px] text-text-2"
        title={absoluteTitle(d.startedAt)}
      >
        {elapsed}
      </span>
    </li>
  )
}

// ── Fleets (order 45) ────────────────────────────────────────────────────────
//
// **Data-driven, never asked.** There is no persona switch and no setting: a *fleet* is simply a
// product that has tenancy (`templateCount > 0`), so an install that never set a template up has no
// fleets, renders no section, and gets exactly the dashboard it got before this existed. An install
// running 40 tenants of one product gets one card instead of 40 — which is the whole point, because a
// 40-card grid of `acme`, `globex`, `initech`… is not a dashboard, it is a list.
//
// This lives in the dashboard module rather than in products or tenancy because it has to **coordinate
// with `StacksGridSection`**: the grid drops exactly the stacks these cards represent, and one owner of
// both halves is the only way that rule can be stated once. Modules never import each other; `lib/api`
// and `lib/release` are shared, which is all this needs.

/** One fleet, joined: the product, and the live rows of the tenants deploying it. */
interface Fleet {
  product: Product
  /**
   * This fleet's tenant stacks, taken from the same `stacks.list` the grid renders rather than from the
   * product roster they were *identified* by. The roster answers membership (which stack is a tenant),
   * the live list answers state (status, pin, deployed release) — so the card and the grid can never
   * disagree about a stack, one shared query, one truth (neither sets a refetch interval; whatever refreshes one refreshes both).
   */
  tenants: Stack[]
}

/**
 * The client-side join behind both the Fleets section and the grid's exclusion rule.
 *
 * Two existing queries and no new RPC: `products.list` says which products have tenancy, and one
 * `products.get` per *fleet* (on the `['product', id]` key the product page itself uses, so the hop
 * from here to there costs nothing) says which of its stacks are tenants — `StackDto` carries no
 * `templateId`, and putting one there would be a backend change for a presentation rule.
 *
 * Both consumers call this; React Query dedupes them onto one request each.
 *
 * `settled` is what keeps the grid from flashing: until every roster has answered, "is this stack a
 * tenant" has no answer, and rendering a tenant card that vanishes a moment later reads as a glitch.
 * Only with the modules off is it true from the very first render; with them on, the grid's first
 * paint additionally waits for `products.list` — one cheap parallel request every dashboard load now
 * makes, fleets or not, because "there are no fleets" is itself an answer only that call can give.
 */
function useFleets(): { fleets: Fleet[]; tenantIds: Set<number>; settled: boolean } {
  // Both modules, deliberately: a fleet is a *tenancy* concept surfaced on a *products* page.
  // `templateCount` is a raw DB count that survives disabling the Tenancy module, and with Tenancy
  // off the Instances tab this card links to is not contributed — the card's only affordance would
  // land on a blank tab body. Gating on both makes the section and the exclusion rule go inert
  // together (the stage-8b precedent: state the two-module dependency in code, don't assume it).
  const caps = dashboardApi.useRouteContext().caps
  const productsEnabled = caps.isModuleEnabled('Products') && caps.isModuleEnabled('Tenancy')

  const productsQuery = useQuery({
    queryKey: ['products'],
    queryFn: api.products.list,
    // The gate that makes this self-adjusting at the query level, not just at the render level: with
    // Products off the dashboard issues exactly the requests it always did.
    enabled: productsEnabled,
  })
  // A failed products.list is deliberately silent: the fleet view is additive, and a second error
  // banner over a dashboard whose stacks grid still works would be noise. No fleets, today's dashboard.
  const fleetProducts = (productsQuery.data ?? []).filter((p) => p.templateCount > 0)

  const rosters = useQueries({
    queries: fleetProducts.map((p) => ({
      queryKey: ['product', p.id],
      queryFn: () => api.products.get(p.id),
    })),
  })

  const { data: stacks = [] } = useQuery({ queryKey: ['stacks'], queryFn: api.stacks.list })
  const liveById = new Map(stacks.map((s) => [s.id, s]))

  const fleets: Fleet[] = []
  const tenantIds = new Set<number>()
  fleetProducts.forEach((product, i) => {
    const roster = rosters[i]?.data
    if (!roster) return
    const tenants: Stack[] = []
    for (const row of roster.stacks) {
      // A standalone stack of a fleet product is not a tenant and keeps its own card — the fleet card
      // does not speak for it, so nothing may hide it.
      if (row.templateId == null) continue
      const live = liveById.get(row.id)
      if (live) tenants.push(live)
    }
    // A tenancy setup with no tenants yet runs nothing, so it gets no card and — if it is the only
    // fleet — no section. Finishing that setup is the product page's job, not the dashboard's.
    if (tenants.length === 0) return
    for (const t of tenants) tenantIds.add(t.id)
    fleets.push({ product, tenants })
  })

  const settled = !productsQuery.isLoading && rosters.every((r) => !r.isLoading)
  return { fleets, tenantIds, settled }
}

/**
 * One card per fleet, between Active deployments and the stacks grid; nothing at all when there are no
 * fleets. Self-contained like every other section.
 */
export function FleetsSection() {
  const { fleets } = useFleets()
  if (fleets.length === 0) return null

  return (
    <section>
      <SectionHeader title="Fleets" />
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 lg:grid-cols-3">
        {fleets.map((fleet) => (
          <FleetCard key={fleet.product.id} fleet={fleet} />
        ))}
      </div>
    </section>
  )
}

function FleetCard({ fleet: { product, tenants } }: { fleet: Fleet }) {
  const latest = product.latestRelease
  // Newest is the highest id (invariant 7); the product's own `latestRelease` is that answer, already
  // on the catalogue row, so the rollup needs no release list.
  const rollup = versionRollup(tenants, latest?.id ?? null)
  const rollupLine = [
    rollup.onLatest > 0 && `${rollup.onLatest} on latest`,
    rollup.pinned > 0 && `${rollup.pinned} pinned`,
    rollup.behind > 0 && `${rollup.behind} behind`,
  ]
    .filter(Boolean)
    .join(' · ')
  // Exactly what `StacksPage`'s failed filter and `StackCard`'s red dot mean, deliberately: this chip
  // is the only thing standing between a tenant's failure and invisibility now that the grid collapses
  // them, so it may not classify more narrowly than the cards it replaces. A tenant that was simply
  // stopped is not failing — its last deploy succeeded — and none of the three counts it.
  const failing = tenants.filter((t) => t.lastDeployStatus === 'failed').length
  const repo = product.repositoryUrl.replace(/^https?:\/\//, '')

  return (
    <Card interactive className="flex flex-col p-4 md:p-5">
      {/* Header: product + repo, the stack card's shape with the catalogue's icon */}
      <div className="flex items-start gap-2.5">
        <Package className="mt-0.5 size-4 shrink-0 text-text-3" aria-hidden />
        <div className="min-w-0 flex-1">
          <Link
            to="/products/$id"
            params={{ id: String(product.id) }}
            className="block truncate text-[15px] font-semibold tracking-tight text-text transition-colors hover:text-brand"
          >
            {product.name}
          </Link>
          <p className="mt-0.5 truncate font-mono text-xs text-text-3" title={repo}>
            {repo}
          </p>
        </div>
      </div>

      {/* Meta line: the stack card's container count, one rung up */}
      <div className="mt-3.5 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-xs text-text-2">
        <span className="inline-flex items-center gap-1">
          <Users className="size-3.5 text-text-3" aria-hidden />
          {tenants.length} tenant{tenants.length === 1 ? '' : 's'}
        </span>
        {/* Silence is healthy: no chip at all when nothing is failing (design.md's drift rule). */}
        {failing > 0 && (
          <Badge tone="danger" size="sm">
            {failing} failing
          </Badge>
        )}
      </div>

      {/* The roster vocabulary, verbatim — the fleet's whole version surface. Invariant 4 keeps it out
          of Git mode, where these three buckets describe nothing: the rollup is the release-mode
          answer to "which instance runs which version", and there is no other update mechanism here. */}
      {product.releaseMode === 'releases' && rollupLine && (
        <p className="mt-2 text-xs text-text-2">{rollupLine}</p>
      )}

      {/* Footer: latest release + the way in. There is deliberately NO Deploy button — every fleet
          action (roll out a release, deploy all, back up all) lives on the product page, where the
          dialog that states its consequence lives too. That also keeps invariant 6 trivially true
          here: a card with no Deploy owes no version beside one. */}
      <div className="mt-4 flex items-center justify-between gap-2">
        <span className="min-w-0 truncate text-xs text-text-3">
          {latest ? (
            <span className="tnum" title={absoluteTitle(latest.createdAt)}>
              <span className="font-medium text-text-2">{latest.version}</span> ·{' '}
              {timeAgo(latest.createdAt)}
            </span>
          ) : (
            <span className="italic">No releases yet</span>
          )}
        </span>
        <Link
          to="/products/$id"
          params={{ id: String(product.id) }}
          search={{ tab: 'instances' }}
          className="shrink-0 text-xs font-medium text-text-2 transition-colors hover:text-brand"
        >
          Open instances →
        </Link>
      </div>
    </Card>
  )
}

// ── Stacks grid (order 50) ───────────────────────────────────────────────────

/**
 * The "Stacks" grid of StackCards (or the empty state). Self-contained: queries the stacks list +
 * containers list (for per-card container counts). Deploy fires from each card with a toast.
 *
 * Since the Fleets section exists this grid renders what a fleet card does *not* speak for — see the
 * exclusion rule below. With no fleets that is every stack, unchanged.
 */
export function StacksGridSection() {
  const qc = useQueryClient()
  const { fleets, tenantIds, settled: fleetsSettled } = useFleets()

  const stacksQuery = useQuery({
    queryKey: ['stacks'],
    queryFn: api.stacks.list,
  })
  const stacks = stacksQuery.data ?? []

  const activeDeploymentsQuery = useQuery({
    queryKey: ['deployments', 'active'],
    queryFn: api.deployments.active,
    refetchInterval: (query) =>
      (query.state.data?.length ?? 0) > 0 ? 2_500 : 10_000,
  })
  const activeDeployments = activeDeploymentsQuery.data ?? []

  // A7: containers poll 10s while anything is active/live, 30s when everything is idle.
  const hasLiveWork =
    activeDeployments.length > 0 ||
    stacks.some(
      (s) => s.lastDeployStatus === 'running' || s.lastDeployStatus === 'queued',
    )
  const { data: containers = [] } = useQuery({
    queryKey: ['containers'],
    queryFn: api.containers.list,
    refetchInterval: hasLiveWork ? 10_000 : 30_000,
  })

  const deploy = useMutation({
    mutationFn: (stack: Stack) => api.stacks.deploy(stack.id),
    onSuccess: (_data, stack) => {
      toast.info(`Deploying ${stack.name}…`)
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['deployments', 'active'] })
    },
    onError: (err: unknown, stack) => {
      toast.error(`Failed to deploy ${stack.name}: ${errMessage(err)}`)
    },
  })

  const containerCountFor = (stack: Stack) =>
    containers.filter((c) => c.stackName === stack.composeProjectName).length

  // Query load error → in-panel danger Banner with Retry (§5).
  if (stacksQuery.isError) {
    return (
      <Banner
        tone="danger"
        title="Couldn't load stacks"
        action={
          <Button
            variant="secondary"
            size="sm"
            onClick={() => stacksQuery.refetch()}
            loading={stacksQuery.isFetching}
          >
            Retry
          </Button>
        }
      >
        {errMessage(stacksQuery.error)}
      </Banner>
    )
  }

  // The fleet join is part of "what does this grid render", so the skeleton covers it too. It settles
  // immediately when there are no fleets to look up, so a hobby install waits for nothing extra.
  if (stacksQuery.isLoading || !fleetsSettled) return <StacksGridSkeleton />

  if (stacks.length === 0) {
    return (
      <EmptyState
        icon={Boxes}
        title="No stacks yet"
        description="Register a git repo with a compose file to start deploying."
        action={
          <Button asChild variant="primary">
            <Link to="/stacks/new">
              <Plus /> New stack
            </Link>
          </Button>
        }
      />
    )
  }

  // ── The exclusion rule ─────────────────────────────────────────────────────
  //
  // A stack that a fleet card already represents does not get a second card here. The card states the
  // fleet's tenant count, its version rollup and — the load-bearing half — its failing count, so
  // nothing a tenant could have told the reader from this grid is lost, and 40 near-identical cards
  // that differ only by customer name stop being the dashboard.
  //
  // It is *membership*, not "has a template id": only tenants of a product that actually rendered a
  // card are dropped. A tenant whose fleet card is missing for any reason — the Products module off,
  // the roster query failed, the fleet filtered out — stays in the grid, because the alternative is a
  // stack that appears nowhere. A detached tenant (no template) was never in the set to begin with.
  const visible = stacks.filter((s) => !tenantIds.has(s.id))
  // Every stack is a tenant: the fleet cards above are the whole story, so an empty "Stacks" heading
  // over an empty grid would be the noise the collapse exists to remove.
  if (visible.length === 0) return null

  return (
    <section>
      {/* Named for what it holds. With fleets on screen this grid is the remainder — standalone
          stacks and detached tenants — and calling that "Stacks" would read as a contradiction of
          the Summary's total two sections up. With no fleets it is the heading it always was. */}
      <SectionHeader title={fleets.length > 0 ? 'Other stacks' : 'Stacks'} />
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 lg:grid-cols-3">
        {visible.map((stack) => (
          <StackCard
            key={stack.id}
            stack={stack}
            containerCount={containerCountFor(stack)}
            onDeploy={() => deploy.mutate(stack)}
            deploying={deploy.isPending && deploy.variables?.id === stack.id}
          />
        ))}
      </div>
    </section>
  )
}

function StackCard({
  stack,
  containerCount,
  onDeploy,
  deploying,
}: {
  stack: Stack
  containerCount: number
  onDeploy: () => void
  deploying: boolean
}) {
  const dotTone = describeDot(stack.lastDeployStatus)
  // `hasUpdates` means different things in the two modes and is the only field that is right in
  // both (StacksContracts.cs): in Releases mode it means "a newer release exists", while
  // `outdatedImages` is empty by construction and `newCommitSha` is informational — unreleased
  // commits on the branch, which no redeploy would pick up. Counting those here badged an
  // up-to-date release-mode stack "1 update".
  const updateCount = usesReleases(stack)
    ? stack.hasUpdates
      ? 1
      : 0
    : (stack.outdatedImages?.length ?? 0) + (stack.newCommitSha ? 1 : 0)
  // Invariant 6: this card offers a Deploy, so it names its target. From the list DTO alone — the
  // dashboard makes no per-stack request.
  const deployTarget = usesReleases(stack) ? deployTargetVersion(stack) : null
  // 'queued' counts: a deploy waiting for its turn (per-stack, or at the instance-wide deploy gate)
  // is in flight as far as this card is concerned, and a second click would create a second deploy.
  const isDeploying = stack.lastDeployStatus === 'running' || stack.lastDeployStatus === 'queued'
  const repo = stack.repositoryUrl.replace(/^https?:\/\//, '')

  return (
    <Card interactive className="flex flex-col p-4 md:p-5">
      {/* Header: dot + name + repo */}
      <div className="flex items-start gap-2.5">
        <span
          className={cn('mt-1.5 size-2 shrink-0 rounded-full', dotTone)}
          aria-hidden
        />
        <div className="min-w-0 flex-1">
          <Link
            to="/stacks/$id"
            params={{ id: String(stack.id) }}
            className="block truncate text-[15px] font-semibold tracking-tight text-text transition-colors hover:text-brand"
          >
            {stack.name}
          </Link>
          <p className="mt-0.5 truncate font-mono text-xs text-text-3" title={repo}>
            {repo}
          </p>
        </div>
      </div>

      {/* Meta line */}
      <div className="mt-3.5 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-xs text-text-2">
        <span className="inline-flex items-center gap-1">
          <ContainerIcon className="size-3.5 text-text-3" aria-hidden />
          {containerCount} container{containerCount === 1 ? '' : 's'}
        </span>
        {updateCount > 0 && (
          <Badge tone="warn" size="sm">
            {updateCount} update{updateCount === 1 ? '' : 's'}
          </Badge>
        )}
      </div>

      {/* Footer: last deployed + Deploy */}
      <div className="mt-4 flex items-center justify-between gap-2">
        <span className="min-w-0 truncate text-xs text-text-3">
          {stack.lastDeployStatus === 'failed' && stack.lastDeployedAt ? (
            <span
              className="tnum inline-flex items-center gap-1 text-danger"
              title={absoluteTitle(stack.lastDeployedAt)}
            >
              <XCircle className="size-3.5" aria-hidden />
              Failed {timeAgo(stack.lastDeployedAt)}
            </span>
          ) : isDeploying ? (
            <span className="text-run">Deploying…</span>
          ) : stack.lastDeployedAt ? (
            <span className="tnum" title={absoluteTitle(stack.lastDeployedAt)}>
              Deployed {timeAgo(stack.lastDeployedAt)}
            </span>
          ) : (
            <span className="italic">Never deployed</span>
          )}
        </span>
        <Button
          variant="secondary"
          size="sm"
          onClick={onDeploy}
          loading={deploying}
          disabled={isDeploying}
          className="shrink-0"
          aria-label={`Deploy ${stack.name}`}
        >
          {!deploying && <Play className="fill-current" />}
          {deployTarget ? `Deploy ${deployTarget}` : 'Deploy'}
        </Button>
      </div>
    </Card>
  )
}

function describeDot(status: Stack['lastDeployStatus']): string {
  switch (status) {
    case 'success':
      return 'bg-ok'
    case 'failed':
      return 'bg-danger'
    case 'running':
      return 'bg-run motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]'
    case 'queued':
      return 'bg-queue motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]'
    default:
      return 'bg-neutral'
  }
}

function StacksGridSkeleton() {
  return (
    <div className="grid grid-cols-1 gap-3 md:grid-cols-2 lg:grid-cols-3">
      {Array.from({ length: 3 }).map((_, i) => (
        <Card key={i} className="p-4 md:p-5">
          <div className="flex items-start gap-2.5">
            <Skeleton variant="circle" className="mt-1 size-2" />
            <div className="flex-1">
              <Skeleton variant="line" className="h-4 w-28" />
              <Skeleton variant="line" className="mt-2 h-3 w-40" />
            </div>
          </div>
          <Skeleton variant="line" className="mt-4 h-3 w-24" />
          <div className="mt-4 flex items-center justify-between">
            <Skeleton variant="line" className="h-3 w-20" />
            <Skeleton variant="rect" className="h-[30px] w-20" />
          </div>
        </Card>
      ))}
    </div>
  )
}

// ── Shared bits ──────────────────────────────────────────────────────────────

/** "● live" chip signalling an active fast-polling interval (A7). */
export function LiveChip() {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full bg-run-bg px-2 py-0.5 text-[11px] font-medium text-run">
      <span className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]" aria-hidden />
      live
    </span>
  )
}

function errMessage(err: unknown): string {
  if (err instanceof Error) return err.message
  if (typeof err === 'string') return err
  return 'Unexpected error'
}
