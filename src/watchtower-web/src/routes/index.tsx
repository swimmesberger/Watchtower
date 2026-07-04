import { useState, useSyncExternalStore } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import {
  ArrowRight,
  Boxes,
  ChevronRight,
  Container as ContainerIcon,
  Info,
  Play,
  Plus,
  XCircle,
} from 'lucide-react'
import { api } from '@/lib/api'
import type {
  ActiveDeployment,
  HostMetrics,
  Stack,
  StackMetrics,
} from '@/lib/types'
import {
  absoluteTitle,
  formatBytes,
  meterTone,
  timeAgo,
  useElapsed,
} from '@/lib/format'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Meter } from '@/components/ui/meter'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { Sparkline } from '@/components/ui/sparkline'
import { StatCard } from '@/components/ui/stat-card'
import { StatusBadge } from '@/components/ui/status-badge'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

/** Path to the host-metrics setup doc (spec §7); a plain anchor to the repo doc. */
const HOST_METRICS_DOC = '/docs/host-metrics.md'

/**
 * Subscribes to document visibility (F6/A7): the metrics poll runs at 5s while the
 * tab is visible and pauses entirely when hidden. SSR-safe (defaults to visible).
 */
function useDocumentVisible(): boolean {
  return useSyncExternalStore(
    (onChange) => {
      document.addEventListener('visibilitychange', onChange)
      return () => document.removeEventListener('visibilitychange', onChange)
    },
    () => !document.hidden,
    () => true,
  )
}

export function DashboardPage() {
  const qc = useQueryClient()

  const stacksQuery = useQuery({
    queryKey: ['stacks'],
    queryFn: api.stacks.list,
  })
  const stacks = stacksQuery.data ?? []

  const activeDeploymentsQuery = useQuery({
    queryKey: ['deployments', 'active'],
    queryFn: api.deployments.active,
    // A7: fast poll (2.5s) while there are active deployments, slow (10s) when idle.
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

  const { data: selfStatus } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: 5 * 60_000,
    retry: false,
  })

  // Metrics poll (§5.1/§5.2 + F6): 5s while the tab is visible, paused on document.hidden.
  const documentVisible = useDocumentVisible()
  const metricsInterval = documentVisible ? 5_000 : (false as const)

  const hostMetricsQuery = useQuery({
    queryKey: ['metrics', 'host'],
    queryFn: api.metrics.host,
    refetchInterval: metricsInterval,
    // Keep polling while the tab is backgrounded is undesirable; pause when hidden.
    refetchIntervalInBackground: false,
  })

  const stackMetricsQuery = useQuery({
    queryKey: ['metrics', 'stacks'],
    queryFn: api.metrics.stacks,
    refetchInterval: metricsInterval,
    refetchIntervalInBackground: false,
  })

  // A7: the metrics poll extends the "● live" chip semantics — it counts as live work
  // whenever the fast 5s interval is active (tab visible).
  const metricsLive = documentVisible

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

  // A7: the "● live" chip shows while any fast interval is active — the 2.5s deployment
  // poll or the 5s metrics poll (the latter runs whenever the tab is visible).
  const isFastPolling = activeDeployments.length > 0 || metricsLive

  const containerCountFor = (stack: Stack) =>
    containers.filter((c) => c.stackName === stack.composeProjectName).length

  const healthy = stacks.filter((s) => s.lastDeployStatus === 'success').length
  const failed = stacks.filter((s) => s.lastDeployStatus === 'failed').length

  return (
    <div className="space-y-6">
      {/* Page header */}
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2.5">
          <h1 className="text-2xl font-semibold tracking-tight text-text">Dashboard</h1>
          {isFastPolling && <LiveChip />}
        </div>
        {/* Desktop primary action; on mobile this becomes the FAB below. */}
        <Button asChild variant="primary" className="hidden md:inline-flex">
          <Link to="/stacks/new">
            <Plus /> New stack
          </Link>
        </Button>
      </div>

      {/* Query load error → in-panel danger Banner with Retry (§5). */}
      {stacksQuery.isError && (
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
      )}

      {/* Self-update banner (dismissible, links to /settings). */}
      {selfStatus?.isOutdated && (
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
      )}

      {/* Host-health strip — the FIRST content block (§5.1). Renders independently of the
          stacks query so "is the host healthy" answers even while stacks load. */}
      <HostHealthStrip
        host={hostMetricsQuery.data}
        isLoading={hostMetricsQuery.isLoading}
        isError={hostMetricsQuery.isError}
        onRetry={() => hostMetricsQuery.refetch()}
        isRetrying={hostMetricsQuery.isFetching}
      />

      {/* Loading skeletons match the page shape (§5). */}
      {stacksQuery.isLoading ? (
        <DashboardSkeleton />
      ) : (
        <>
          {/* Stat cards — 2×2 on mobile, 4-up on desktop (A5 links). */}
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

          {/* Active deployments — only when non-empty. */}
          {activeDeployments.length > 0 && (
            <section>
              <SectionHeader
                title="Active deployments"
                action={<LiveChip />}
              />
              <Card>
                <ul className="divide-y divide-border">
                  {activeDeployments.map((d) => (
                    <ActiveDeploymentRow key={d.id} deployment={d} />
                  ))}
                </ul>
              </Card>
            </section>
          )}

          {/* Resource usage — the "who eats the resources" answer (§5.2 + F8). Sits above
              the Stacks grid; container stats are independent of host /proc availability. */}
          <ResourceUsageSection
            data={stackMetricsQuery.data?.stacks}
            isLoading={stackMetricsQuery.isLoading}
            isError={stackMetricsQuery.isError}
            onRetry={() => stackMetricsQuery.refetch()}
            isRetrying={stackMetricsQuery.isFetching}
            resolveStackId={(project) =>
              stacks.find((s) => s.composeProjectName === project)?.id ?? null
            }
          />

          {/* Stacks grid or empty state. */}
          {stacks.length === 0 ? (
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
          ) : (
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
          )}
        </>
      )}

      {/* Mobile FAB → New stack (sticky above the bottom tab bar). */}
      <Link
        to="/stacks/new"
        aria-label="New stack"
        className={cn(
          'fixed bottom-[calc(var(--bottombar-h)+env(safe-area-inset-bottom)+16px)] right-4 z-20',
          'flex size-14 items-center justify-center rounded-full bg-brand text-brand-fg shadow-[var(--sh-lg)]',
          'transition-colors hover:bg-[var(--brand-hover)] active:translate-y-px',
          'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
          'md:hidden',
        )}
      >
        <Plus className="size-6" />
      </Link>
    </div>
  )
}

/** "● live" chip signalling an active fast-polling interval (A7). */
function LiveChip() {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full bg-run-bg px-2 py-0.5 text-[11px] font-medium text-run">
      <span className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]" aria-hidden />
      live
    </span>
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

function DashboardSkeleton() {
  return (
    <>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <Card key={i} className="p-4">
            <Skeleton variant="line" className="h-3 w-16" />
            <Skeleton variant="line" className="mt-3 h-7 w-10" />
          </Card>
        ))}
      </div>
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
    </>
  )
}

// ── Host-health strip (§5.1) ─────────────────────────────────────────────────

/** Footer link into the fleet-wide Infrastructure view (§1 mobile path, §5.1). */
function InfraFooterLink() {
  return (
    <div className="mt-4 border-t border-border pt-3">
      <Link
        to="/infrastructure"
        className="inline-flex items-center gap-1 text-[13px] font-medium text-text-2 transition-colors hover:text-brand"
      >
        View all volumes &amp; networks
        <ArrowRight className="size-3.5" aria-hidden />
      </Link>
    </div>
  )
}

/**
 * The host-health strip: a single Card, first content block. Desktop 4-up
 * (CPU · RAM · Load · Disk), mobile 2×2. Degrades to an info Banner when host /proc
 * isn't mounted, while the Disk cell may still render from docker-df (§5.1).
 */
function HostHealthStrip({
  host,
  isLoading,
  isError,
  onRetry,
  isRetrying,
}: {
  host: HostMetrics | undefined
  isLoading: boolean
  isError: boolean
  onRetry: () => void
  isRetrying: boolean
}) {
  // Query error → persistent in-panel danger Banner + Retry (§5.5).
  if (isError) {
    return (
      <Card className="p-4 md:p-5">
        <Banner
          tone="danger"
          title="Couldn't load host metrics"
          action={
            <Button variant="secondary" size="sm" onClick={onRetry} loading={isRetrying}>
              Retry
            </Button>
          }
        >
          The host-health strip couldn't be loaded. Container metrics may still work below.
        </Banner>
      </Card>
    )
  }

  // Loading → 4 skeleton cells (§5.5).
  if (isLoading || !host) {
    return (
      <Card className="p-4 md:p-5">
        <div className="grid grid-cols-2 gap-x-6 gap-y-5 md:grid-cols-4 md:divide-x md:divide-border">
          {Array.from({ length: 4 }).map((_, i) => (
            <div key={i} className="md:px-5 md:first:pl-0 md:last:pr-0">
              <Skeleton variant="line" className="h-3 w-12" />
              <Skeleton variant="line" className="mt-3 h-7 w-16" />
              <Skeleton variant="line" className="mt-2 h-3 w-14" />
            </div>
          ))}
        </div>
      </Card>
    )
  }

  const cpuLabel = host.cpuCores != null ? `${host.cpuCores} cores` : undefined
  const loadWarn =
    host.loadAvg1 != null && host.cpuCores != null && host.loadAvg1 > host.cpuCores
  const cpuHistory = host.history.map((h) => h.cpuPercent ?? 0)
  const memHistory = host.history.map((h) => h.memPercent ?? 0)

  // Disk can still populate from docker-df even when host /proc is absent (§5.1).
  const diskFromDockerDf = host.diskSource === 'docker-df'
  const diskAvailable = host.diskPercent != null || host.diskUsedBytes != null

  // Degraded: host /proc not mounted → CPU/RAM/Load collapse to an info Banner; the Disk
  // cell may still render (docker-df fallback).
  if (!host.available) {
    return (
      <Card className="p-4 md:p-5">
        <Banner
          tone="info"
          icon={Info}
          title="Host metrics unavailable"
          action={
            <a
              href={HOST_METRICS_DOC}
              className="inline-flex items-center gap-1 text-[13px] font-medium text-brand transition-colors hover:text-[var(--brand-hover)]"
            >
              Enable host metrics
              <ArrowRight className="size-3.5" aria-hidden />
            </a>
          }
        >
          Watchtower can't read the host's CPU and memory because{' '}
          <code className="font-mono text-[12px]">/proc</code> isn't mounted into its
          container. Container metrics still work.
        </Banner>

        {diskAvailable && (
          <div className="mt-4 border-t border-border pt-4 md:w-1/2 md:pr-5">
            <HostCell
              label="Disk"
              percent={host.diskPercent}
              value={
                host.diskPercent != null
                  ? `${Math.round(host.diskPercent)}%`
                  : host.diskUsedBytes != null
                    ? formatBytes(host.diskUsedBytes)
                    : '—'
              }
              sub={
                host.diskUsedBytes != null && host.diskTotalBytes != null
                  ? `${formatBytes(host.diskUsedBytes)} / ${formatBytes(host.diskTotalBytes)}`
                  : undefined
              }
              valueTooltip={
                diskFromDockerDf
                  ? "Docker's view of disk, not the full host."
                  : undefined
              }
              note={diskFromDockerDf ? 'docker-df' : undefined}
            />
          </div>
        )}

        <InfraFooterLink />
      </Card>
    )
  }

  return (
    <Card className="p-4 md:p-5">
      <div className="grid grid-cols-2 gap-x-6 gap-y-5 md:grid-cols-4 md:divide-x md:divide-border">
        {/* CPU */}
        <HostCell
          className="md:px-5 md:first:pl-0"
          label="CPU"
          percent={host.cpuPercent}
          value={host.cpuPercent != null ? `${Math.round(host.cpuPercent)}%` : '—'}
          sub={cpuLabel}
          spark={cpuHistory}
        />
        {/* RAM */}
        <HostCell
          className="md:px-5"
          label="RAM"
          percent={host.memPercent}
          value={host.memPercent != null ? `${Math.round(host.memPercent)}%` : '—'}
          sub={
            host.memUsedBytes != null && host.memTotalBytes != null
              ? `${formatBytes(host.memUsedBytes)} / ${formatBytes(host.memTotalBytes)}`
              : undefined
          }
          spark={memHistory}
        />
        {/* Load — warn when load1 > cores (§5.1). */}
        <HostCell
          className="md:px-5"
          label="Load"
          value={host.loadAvg1 != null ? host.loadAvg1.toFixed(2) : '—'}
          tone={loadWarn ? 'warn' : undefined}
          sub={host.loadAvg5 != null ? `5m ${host.loadAvg5.toFixed(2)}` : undefined}
          valueTooltip={
            host.cpuCores != null
              ? `1-min load average · ${host.cpuCores} cores`
              : '1-min load average'
          }
        />
        {/* Disk — HostSample carries no disk history, so this cell shows value + %, no
            sparkline; the docker-df provenance rides a Tooltip on the value (§5.1). */}
        <HostCell
          className="md:px-5 md:last:pr-0"
          label="Disk"
          percent={host.diskPercent}
          value={
            host.diskPercent != null
              ? `${Math.round(host.diskPercent)}%`
              : host.diskUsedBytes != null
                ? formatBytes(host.diskUsedBytes)
                : '—'
          }
          sub={
            host.diskUsedBytes != null && host.diskTotalBytes != null
              ? `${formatBytes(host.diskUsedBytes)} / ${formatBytes(host.diskTotalBytes)}`
              : undefined
          }
          valueTooltip={
            diskFromDockerDf ? "Docker's view of disk, not the full host." : undefined
          }
          note={diskFromDockerDf ? 'docker-df' : undefined}
        />
      </div>

      <InfraFooterLink />
    </Card>
  )
}

/**
 * One cell of the host-health strip: xs uppercase label · big tnum value (threshold-colored) ·
 * optional sub-line · optional Sparkline. Thresholds via `meterTone` unless `tone` is given
 * (Load uses a fixed warn from load>cores).
 */
function HostCell({
  label,
  value,
  sub,
  percent,
  spark,
  tone,
  valueTooltip,
  note,
  className,
}: {
  label: string
  value: string
  sub?: string
  percent?: number | null
  spark?: number[]
  tone?: 'warn' | 'danger'
  /** Tooltip attached to the value (e.g. Load average detail, docker-df provenance). */
  valueTooltip?: string
  /** Small caption under the sub-line (e.g. the "docker-df" disk source). */
  note?: string
  className?: string
}) {
  const resolved = tone ?? (percent != null ? meterTone(percent) : 'ok')
  const valueColor =
    resolved === 'danger' ? 'text-danger' : resolved === 'warn' ? 'text-warn' : 'text-text'

  const valueEl = (
    <span
      className={cn('tnum text-2xl font-semibold leading-none tracking-tight', valueColor)}
    >
      {value}
    </span>
  )

  return (
    <div className={className}>
      <p className="text-[11px] font-medium uppercase tracking-wide text-text-3">{label}</p>
      <div className="mt-2 flex items-end gap-2.5">
        {valueTooltip ? (
          <Tooltip label={valueTooltip}>
            <button
              type="button"
              className="cursor-default rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--brand-ring)]"
            >
              {valueEl}
            </button>
          </Tooltip>
        ) : (
          valueEl
        )}
        {spark != null && (
          <Sparkline data={spark} tone={tone} aria-label={`${label} trend`} />
        )}
      </div>
      {sub && <p className="tnum mt-1 text-xs text-text-2">{sub}</p>}
      {note && <p className="mt-1 text-[11px] text-text-3">{note}</p>}
    </div>
  )
}

// ── Resource-usage ranking (§5.2 + F8) ───────────────────────────────────────

type ResourceDimension = 'cpu' | 'ram'

/**
 * The per-stack resource ranking. `metrics.stacks` arrives CPU-sorted; the F8 CPU|RAM
 * toggle re-sorts client-side (StackMetrics carries both). Biggest consumer on top.
 */
function ResourceUsageSection({
  data,
  isLoading,
  isError,
  onRetry,
  isRetrying,
  resolveStackId,
}: {
  data: StackMetrics[] | undefined
  isLoading: boolean
  isError: boolean
  onRetry: () => void
  isRetrying: boolean
  resolveStackId: (project: string) => number | null
}) {
  const [dimension, setDimension] = useState<ResourceDimension>('cpu')

  const sorted =
    data == null
      ? []
      : [...data].sort((a, b) =>
          dimension === 'cpu'
            ? b.cpuPercent - a.cpuPercent
            : b.memUsedBytes - a.memUsedBytes,
        )

  const maxMem = sorted.reduce((m, s) => Math.max(m, s.memUsedBytes), 0)

  return (
    <section>
      <SectionHeader
        title="Resource usage"
        action={
          data != null &&
          data.length > 0 && (
            <SegmentedToggle value={dimension} onChange={setDimension} />
          )
        }
      />

      {isError ? (
        // Container-stats error → in-panel danger Banner + Retry (§5.2/§5.5).
        <Banner
          tone="danger"
          title="Couldn't load resource usage"
          action={
            <Button variant="secondary" size="sm" onClick={onRetry} loading={isRetrying}>
              Retry
            </Button>
          }
        >
          Docker may be unreachable. Host metrics above are independent.
        </Banner>
      ) : isLoading || data == null ? (
        <Card>
          <ul className="divide-y divide-border">
            {Array.from({ length: 3 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 p-4 md:px-5">
                <Skeleton variant="line" className="h-4 w-24 shrink-0" />
                <Skeleton variant="rect" className="h-1.5 flex-1" />
                <Skeleton variant="line" className="h-4 w-12 shrink-0" />
              </li>
            ))}
          </ul>
        </Card>
      ) : sorted.length === 0 ? (
        <Card className="p-6">
          <p className="text-center text-sm text-text-3">No running containers.</p>
        </Card>
      ) : (
        <Card>
          <ul className="divide-y divide-border">
            {sorted.map((stack) => (
              <ResourceRow
                key={stack.stackName}
                stack={stack}
                dimension={dimension}
                maxMem={maxMem}
                stackId={resolveStackId(stack.stackName)}
              />
            ))}
          </ul>
        </Card>
      )}
    </section>
  )
}

/** F8 CPU|RAM segmented control — two minimal ghost buttons; default CPU. */
function SegmentedToggle({
  value,
  onChange,
}: {
  value: ResourceDimension
  onChange: (d: ResourceDimension) => void
}) {
  return (
    <div
      role="group"
      aria-label="Sort by"
      className="inline-flex items-center gap-0.5 rounded-md border border-border bg-surface-2 p-0.5"
    >
      {(['cpu', 'ram'] as const).map((dim) => (
        <button
          key={dim}
          type="button"
          aria-pressed={value === dim}
          onClick={() => onChange(dim)}
          className={cn(
            'rounded px-2.5 py-1 text-xs font-medium transition-colors',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--brand-ring)]',
            value === dim
              ? 'bg-surface text-text shadow-[var(--sh-sm)]'
              : 'text-text-3 hover:text-text-2',
          )}
        >
          {dim === 'cpu' ? 'CPU' : 'RAM'}
        </button>
      ))}
    </div>
  )
}

/**
 * One row of the resource ranking: stack link · Meter (selected dimension) · tnum cpu/mem ·
 * Sparkline · chevron. The whole row is an interactive link into the stack.
 */
function ResourceRow({
  stack,
  dimension,
  maxMem,
  stackId,
}: {
  stack: StackMetrics
  dimension: ResourceDimension
  maxMem: number
  /** Resolved from the compose project; null when no matching registered stack. */
  stackId: number | null
}) {
  const cpuText = `${Math.round(stack.cpuPercent)}%`
  const memText = formatBytes(stack.memUsedBytes)

  // Meter + sparkline follow the selected dimension (F8).
  const meterValue = dimension === 'cpu' ? stack.cpuPercent : stack.memUsedBytes
  const meterMax = dimension === 'cpu' ? 100 : maxMem || 1
  const meterTonePicked = dimension === 'cpu' ? undefined : ('brand' as const)

  const sparkData =
    dimension === 'cpu'
      ? stack.history.map((h) => h.cpuPercent)
      : stack.history.map((h) => h.memUsedBytes)
  const sparkNormalize: '0-100' | 'auto' = dimension === 'cpu' ? '0-100' : 'auto'
  const sparkTone = dimension === 'cpu' ? undefined : ('brand' as const)

  const rowClass =
    'group flex items-center gap-3 p-4 transition-colors hover:bg-surface-2 md:px-5'

  const inner = (
    <>
      <div className="flex min-w-0 flex-[2] items-center gap-2">
        <span
          className={cn(
            'truncate text-sm font-medium text-text',
            stackId != null && 'group-hover:text-brand',
          )}
        >
          {stack.stackName}
        </span>
        <span className="hidden shrink-0 text-xs text-text-3 md:inline">
          {stack.containerCount} container{stack.containerCount === 1 ? '' : 's'}
        </span>
      </div>

      <div className="hidden flex-[2] md:block">
        <Meter
          value={meterValue}
          max={meterMax}
          tone={meterTonePicked}
          aria-label={`${stack.stackName} ${dimension === 'cpu' ? 'CPU' : 'memory'} usage`}
        />
      </div>

      <span className="tnum w-12 shrink-0 text-right text-sm text-text-2">
        {dimension === 'cpu' ? cpuText : memText}
      </span>
      <span className="tnum hidden w-20 shrink-0 text-right text-sm text-text-3 sm:inline">
        {dimension === 'cpu' ? memText : cpuText}
      </span>

      <span className="hidden shrink-0 sm:inline">
        <Sparkline
          data={sparkData}
          normalize={sparkNormalize}
          tone={sparkTone}
          aria-label={`${stack.stackName} trend`}
        />
      </span>

      {stackId != null && (
        <ChevronRight
          className="size-4 shrink-0 text-text-3 transition-colors group-hover:text-text-2"
          aria-hidden
        />
      )}
    </>
  )

  return (
    <li>
      {stackId != null ? (
        <Link to="/stacks/$id" params={{ id: String(stackId) }} className={rowClass}>
          {inner}
        </Link>
      ) : (
        <div className={rowClass}>{inner}</div>
      )}
    </li>
  )
}

function errMessage(err: unknown): string {
  if (err instanceof Error) return err.message
  if (typeof err === 'string') return err
  return 'Unexpected error'
}
