// The dashboard-owned sections. Each is self-contained: it runs its own queries and owns its own
// loading/empty/error states, so the DashboardPage host can render it blindly in contribution order.
// (The sibling metrics module contributes the host-health strip + resource-usage ranking separately.)
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import {
  Boxes,
  Container as ContainerIcon,
  Play,
  Plus,
  XCircle,
} from 'lucide-react'
import { api } from '@/lib/api'
import type { ActiveDeployment, Stack } from '@/lib/types'
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

// ── Stacks grid (order 50) ───────────────────────────────────────────────────

/**
 * The "Stacks" grid of StackCards (or the empty state). Self-contained: queries the stacks list +
 * containers list (for per-card container counts). Deploy fires from each card with a toast.
 */
export function StacksGridSection() {
  const qc = useQueryClient()

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

  if (stacksQuery.isLoading) return <StacksGridSkeleton />

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

  return (
    <section>
      <SectionHeader title="Stacks" />
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 lg:grid-cols-3">
        {stacks.map((stack) => (
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
  const updateCount = stack.outdatedImages?.length ?? 0
  const isDeploying = stack.lastDeployStatus === 'running'
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
          Deploy
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
