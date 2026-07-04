import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link, useNavigate, useParams } from '@tanstack/react-router'
import {
  Boxes,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Copy,
  Database,
  HardDrive,
  Lock,
  MoreHorizontal,
  Network,
  Play,
  RefreshCw,
  RotateCcw,
  Square,
  Trash2,
} from 'lucide-react'
import { api } from '@/lib/api'
import { apiBase } from '@/lib/config'
import type {
  Container,
  ContainerMetrics,
  Credential,
  DeployEvent,
  NetworkInfo,
  PortConflict,
  PublishedPort,
  ResourceLifecycle,
  Stack,
  StackEnvVar,
  StackEnvVarInput,
  UpdateStackRequest,
  VolumeInfo,
  VolumeSize,
} from '@/lib/types'
import { absoluteTitle, formatBytes, formatDuration, meterTone, timeAgo } from '@/lib/format'
import { cn } from '@/lib/utils'
import { ContainerLogs } from '@/components/container-logs'
import { EnvVarEditor } from '@/components/env-var-editor'
import { Badge, type BadgeTone } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { CopyButton } from '@/components/ui/copy-button'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { ExposureBadge } from '@/components/ui/exposure-badge'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { LiveLog } from '@/components/ui/live-log'
import { Meter } from '@/components/ui/meter'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { SecretField } from '@/components/ui/secret-field'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { Sparkline } from '@/components/ui/sparkline'
import { StatusBadge } from '@/components/ui/status-badge'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

const NO_CREDENTIAL = 'none'

const routeApi = getRouteApi('/stacks/$id')

type StackDetailTab = 'overview' | 'volumes' | 'networks' | 'settings'

/** Lifecycle chip mapping (F4): live→ok, declared→neutral, orphaned→warn. */
const LIFECYCLE_META: Record<ResourceLifecycle, { tone: BadgeTone; label: string }> = {
  live: { tone: 'ok', label: 'live' },
  declared: { tone: 'neutral', label: 'declared' },
  orphaned: { tone: 'warn', label: 'orphaned' },
}

function LifecycleBadge({ lifecycle }: { lifecycle: ResourceLifecycle }) {
  const meta = LIFECYCLE_META[lifecycle]
  return (
    <Badge tone={meta.tone} size="sm">
      {meta.label}
    </Badge>
  )
}

/** A small "● live" chip reused by rows affected by an active deploy/recreate. */
function LiveChip({ label = 'live' }: { label?: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold text-run">
      <span
        className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]"
        aria-hidden
      />
      {label}
    </span>
  )
}

function webhookUrl(stackId: number): string {
  const base = apiBase || (typeof window !== 'undefined' ? window.location.origin : '')
  return `${base}/api/webhooks/stacks/${stackId}/deploy`
}

/** True while the tab is visible; flips on visibilitychange so polling pauses when hidden (A7). */
function useDocumentVisible(): boolean {
  const [visible, setVisible] = useState(
    () => typeof document === 'undefined' || document.visibilityState !== 'hidden',
  )
  useEffect(() => {
    const onChange = () => setVisible(document.visibilityState !== 'hidden')
    document.addEventListener('visibilitychange', onChange)
    return () => document.removeEventListener('visibilitychange', onChange)
  }, [])
  return visible
}

export function StackDetailPage() {
  const { id } = useParams({ from: '/stacks/$id' })
  const stackId = Number(id)
  const qc = useQueryClient()

  // Tab state lives in the URL via ?tab= (F9). Default overview; navigate replace:true.
  const { tab } = routeApi.useSearch()
  const navigateTab = routeApi.useNavigate()
  const activeTab: StackDetailTab = tab ?? 'overview'
  const setTab = useCallback(
    (next: string) => {
      navigateTab({
        search: (prev) => ({ ...prev, tab: next === 'overview' ? undefined : (next as StackDetailTab) }),
        replace: true,
      })
    },
    [navigateTab],
  )

  // Ref registry: deploy-history rows register a focus/expand handler here so the
  // "View log" action on the failure banner can scroll to + expand the latest failed row.
  const historyControls = useRef(new Map<number, { expand: () => void; scrollTo: () => void }>())
  const registerHistoryRow = useCallback(
    (eventId: number, controls: { expand: () => void; scrollTo: () => void }) => {
      historyControls.current.set(eventId, controls)
      return () => {
        historyControls.current.delete(eventId)
      }
    },
    [],
  )

  const {
    data: stack,
    isLoading: stackLoading,
    isError: stackError,
    refetch: refetchStack,
  } = useQuery({
    queryKey: ['stacks', stackId],
    queryFn: () => api.stacks.get(stackId),
    refetchInterval: (q) => {
      const s = q.state.data?.lastDeployStatus
      return s === 'running' || s === 'queued' ? 3000 : false
    },
  })

  const { data: containers = [] } = useQuery({
    queryKey: ['containers'],
    queryFn: api.containers.list,
    refetchInterval: 10_000,
  })

  const isDeploying = stack?.lastDeployStatus === 'running' || stack?.lastDeployStatus === 'queued'

  // Per-container metrics (§5.3): polled 5s while the Overview tab is visible and the tab is
  // not hidden (A7 idle-backoff). Keyed by compose project so the server pre-filters.
  const project = stack?.composeProjectName
  const documentVisible = useDocumentVisible()
  const metricsActive = activeTab === 'overview' && documentVisible
  const { data: containerMetrics = [] } = useQuery({
    queryKey: ['metrics', 'containers', project],
    queryFn: () => api.metrics.containers(project ?? null),
    enabled: !!project && metricsActive,
    refetchInterval: metricsActive ? 5_000 : false,
  })
  const metricsByName = useMemo(() => {
    const map = new Map<string, ContainerMetrics>()
    // Normalize the leading slash so lookups match the ContainerCard's stripped name.
    for (const m of containerMetrics) map.set(m.containerName.replace(/^\//, ''), m)
    return map
  }, [containerMetrics])

  const { data: events = [] } = useQuery({
    queryKey: ['stacks', stackId, 'events'],
    queryFn: () => api.stacks.events(stackId),
    refetchInterval: isDeploying ? 3000 : false,
  })

  const { data: envVars = [] } = useQuery({
    queryKey: ['stacks', stackId, 'env'],
    queryFn: () => api.stacks.getEnv(stackId),
  })

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const deploy = useMutation({
    mutationFn: () => api.stacks.deploy(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'events'] })
      toast.info(`Deploying ${stack?.name ?? 'stack'}…`)
    },
    onError: (err: Error) => toast.error('Deploy failed', err.message),
  })

  function viewFailedLog() {
    // Find the most recent failed event and expand + scroll to it.
    const failed = [...events].find((e) => e.status === 'failed')
    if (!failed) return
    const controls = historyControls.current.get(failed.id)
    controls?.expand()
    // Let the row render its panel before scrolling.
    requestAnimationFrame(() => controls?.scrollTo())
  }

  if (stackLoading) return <StackDetailSkeleton />

  if (stackError || !stack) {
    return (
      <div className="mx-auto max-w-[1200px]">
        <Banner
          tone="danger"
          title="Couldn’t load this stack"
          action={
            <Button variant="secondary" size="sm" onClick={() => refetchStack()}>
              Retry
            </Button>
          }
        >
          The stack may have been deleted, or the server is unreachable.
        </Banner>
      </div>
    )
  }

  const stackContainers = containers.filter((c) => c.stackName === stack.composeProjectName)

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 pb-24 md:pb-0">
      {/* Breadcrumb */}
      <nav aria-label="Breadcrumb" className="flex items-center gap-1 text-xs text-text-2">
        <Link
          to="/stacks"
          className="inline-flex items-center gap-1 rounded transition-colors hover:text-text focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
        >
          <ChevronRight className="size-3.5 rotate-180" aria-hidden />
          Stacks
        </Link>
        <span aria-hidden className="text-text-3">
          /
        </span>
        <span className="truncate font-medium text-text">{stack.name}</span>
      </nav>

      {/* Header */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <h1 className="truncate text-2xl font-semibold tracking-tight text-text">{stack.name}</h1>
          <p className="mt-1 truncate font-mono text-[12.5px] text-text-2">
            {stack.repositoryUrl} · {stack.branch} · {stack.composeFilePath}
          </p>
        </div>
        {/* Desktop deploy button; mobile uses the FAB below. */}
        <Button
          variant="primary"
          loading={deploy.isPending || isDeploying}
          disabled={deploy.isPending || isDeploying}
          onClick={() => deploy.mutate()}
          className="hidden md:inline-flex"
        >
          {!(deploy.isPending || isDeploying) && <Play />}
          Deploy
        </Button>
      </div>

      {/* Status banner hero */}
      {isDeploying ? (
        <Banner tone="info" title="Deployment in progress…">
          Watchtower is pulling images and (re)starting containers.
        </Banner>
      ) : stack.lastDeployStatus === 'success' ? (
        <Banner tone="ok" title="Last deploy succeeded" />
      ) : stack.lastDeployStatus === 'failed' ? (
        <Banner
          tone="danger"
          title="Last deploy failed"
          action={
            <Button variant="secondary" size="sm" onClick={viewFailedLog}>
              View log
            </Button>
          }
        />
      ) : null}

      {/* Tabs (state in ?tab=, F9) */}
      <Tabs value={activeTab} onValueChange={setTab}>
        <TabsList>
          <TabsTrigger value="overview">Overview</TabsTrigger>
          <TabsTrigger value="volumes">Volumes</TabsTrigger>
          <TabsTrigger value="networks">Networks</TabsTrigger>
          <TabsTrigger value="settings">Settings</TabsTrigger>
        </TabsList>

        <TabsContent value="overview">
          <div className="space-y-8">
            {/* Containers */}
            <section>
              <SectionHeader
                title="Containers"
                action={<span className="tnum text-sm text-text-2">{stackContainers.length}</span>}
              />
              {stackContainers.length === 0 ? (
                <EmptyState
                  icon={Boxes}
                  title="No containers running"
                  description="Deploy this stack to see its containers."
                  action={
                    <Button
                      variant="primary"
                      loading={deploy.isPending || isDeploying}
                      disabled={deploy.isPending || isDeploying}
                      onClick={() => deploy.mutate()}
                    >
                      {!(deploy.isPending || isDeploying) && <Play />}
                      Deploy
                    </Button>
                  }
                />
              ) : (
                <div className="space-y-3">
                  {stackContainers.map((container) => (
                    <ContainerCard
                      key={container.id}
                      container={container}
                      metrics={metricsByName.get(
                        container.names[0]?.replace(/^\//, '') ?? '',
                      )}
                    />
                  ))}
                </div>
              )}
            </section>

            {/* Image updates */}
            <section>
              <ImageUpdatesPanel
                stack={stack}
                onChecked={(updated) => qc.setQueryData(['stacks', stackId], updated)}
              />
            </section>

            {/* Webhook */}
            {stack.webhookEnabled && (
              <section>
                <SectionHeader title="Webhook" />
                <WebhookCard stackId={stackId} token={stack.webhookToken} />
              </section>
            )}

            {/* Deploy history */}
            <section>
              <SectionHeader
                title="Deploy history"
                action={<span className="tnum text-sm text-text-2">{events.length}</span>}
              />
              {events.length === 0 ? (
                <p className="text-sm text-text-3">No deployments yet</p>
              ) : (
                <div className="space-y-2">
                  {events.map((event) => (
                    <DeployEventRow key={event.id} event={event} register={registerHistoryRow} />
                  ))}
                </div>
              )}
            </section>
          </div>
        </TabsContent>

        <TabsContent value="volumes">
          <VolumesTab stack={stack} isDeploying={isDeploying} />
        </TabsContent>

        <TabsContent value="networks">
          <NetworksTab stack={stack} />
        </TabsContent>

        <TabsContent value="settings">
          <SettingsTab
            stackId={stackId}
            stack={stack}
            envVars={envVars}
            credentials={credentials}
          />
        </TabsContent>
      </Tabs>

      {/* Mobile Deploy FAB (52px, above the bottom tab bar) */}
      <div className="fixed bottom-bottombar right-4 z-20 mb-3 md:hidden">
        <Button
          variant="primary"
          aria-label="Deploy stack"
          loading={deploy.isPending || isDeploying}
          disabled={deploy.isPending || isDeploying}
          onClick={() => deploy.mutate()}
          className="size-[52px] rounded-full p-0 shadow-[var(--sh-lg)]"
        >
          {!(deploy.isPending || isDeploying) && <Play />}
        </Button>
      </div>
    </div>
  )
}

// ── Container card ────────────────────────────────────────────────────────────

function ContainerCard({
  container,
  metrics,
}: {
  container: Container
  metrics?: ContainerMetrics
}) {
  const qc = useQueryClient()
  const [confirmRemove, setConfirmRemove] = useState(false)
  const name = container.names[0]?.replace(/^\//, '') ?? container.id.slice(0, 12)

  const invalidate = () => qc.invalidateQueries({ queryKey: ['containers'] })

  const restart = useMutation({
    mutationFn: () => api.containers.restart(container.id),
    onSuccess: () => {
      invalidate()
      toast.success(`Restarted ${name}.`)
    },
    onError: (err: Error) => toast.error('Restart failed', err.message),
  })

  const stop = useMutation({
    mutationFn: () => api.containers.stop(container.id),
    onSuccess: () => {
      invalidate()
      toast.success(`Stopped ${name}.`)
    },
    onError: (err: Error) => toast.error('Stop failed', err.message),
  })

  const remove = useMutation({
    mutationFn: () => api.containers.remove(container.id),
    onSuccess: () => {
      invalidate()
      toast.success(`Removed ${name}.`)
    },
    onError: (err: Error) => toast.error('Remove failed', err.message),
    onSettled: () => setConfirmRemove(false),
  })

  const statusValue = container.health ?? container.state
  const running = container.state === 'running'

  return (
    <Card>
      <div className="flex items-center justify-between gap-3 border-b border-border p-4 md:px-5">
        <div className="flex min-w-0 items-center gap-3">
          <span className="truncate text-sm font-semibold text-text">{name}</span>
          <StatusBadge status={statusValue} size="sm" pulse={running || undefined} />
        </div>

        {/* Desktop icon actions */}
        <div className="hidden items-center gap-1 md:flex">
          <Tooltip label="Restart">
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label={`Restart ${name}`}
              loading={restart.isPending}
              onClick={() => restart.mutate()}
            >
              {!restart.isPending && <RotateCcw />}
            </Button>
          </Tooltip>
          <Tooltip label="Stop">
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label={`Stop ${name}`}
              loading={stop.isPending}
              onClick={() => stop.mutate()}
            >
              {!stop.isPending && <Square />}
            </Button>
          </Tooltip>
          <Tooltip label="Remove">
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label={`Remove ${name}`}
              className="text-text-2 hover:text-danger"
              onClick={() => setConfirmRemove(true)}
            >
              <Trash2 />
            </Button>
          </Tooltip>
        </div>

        {/* Mobile overflow menu */}
        <div className="md:hidden">
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${name}`}>
                <MoreHorizontal />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent>
              <DropdownMenuItem onSelect={() => restart.mutate()}>
                <RotateCcw /> Restart
              </DropdownMenuItem>
              <DropdownMenuItem onSelect={() => stop.mutate()}>
                <Square /> Stop
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem destructive onSelect={() => setConfirmRemove(true)}>
                <Trash2 /> Remove
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>

      <CardContent className="pt-4">
        <div className="grid grid-cols-1 gap-x-6 gap-y-3 sm:grid-cols-2">
          <Meta label="Image" value={container.image} />
          <Meta label="Status" value={container.status} />
        </div>
        <ContainerMetricsRow metrics={metrics} online={running} />
        <div className="mt-4">
          <ContainerLogs containerId={container.id} containerName={name} />
        </div>
      </CardContent>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Remove ${name}?`}
        description="This removes the container. It will be recreated on the next deploy."
        confirmLabel="Remove"
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </Card>
  )
}

function Meta({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <p className="text-xs uppercase tracking-[0.04em] text-text-3">{label}</p>
      <p className="mt-0.5 truncate font-mono text-[12.5px] text-text-2">{value}</p>
    </div>
  )
}

// ── Per-container metrics row (§5.3) ─────────────────────────────────────────────

/**
 * Compact CPU% + Sparkline + mem row inside each ContainerCard. Renders "— · stopped"
 * when the container isn't running (online=false). Sparkline uses the 48×16 container
 * spec; mem % drives a threshold-colored Meter.
 */
function ContainerMetricsRow({
  metrics,
  online,
}: {
  metrics?: ContainerMetrics
  online: boolean
}) {
  if (!online || metrics?.online === false) {
    return (
      <div className="mt-3 flex items-center gap-2 border-t border-border pt-3 text-[13px] text-text-3">
        <span className="tnum">—</span>
        <span>stopped</span>
      </div>
    )
  }

  if (!metrics) {
    // Metrics not yet loaded (first poll): a thin skeleton line, never a spinner (§5.5).
    return (
      <div className="mt-3 border-t border-border pt-3">
        <Skeleton variant="line" className="h-4 w-2/3" />
      </div>
    )
  }

  const cpuHistory = metrics.history.map((h) => h.cpuPercent)
  const memPct = metrics.memPercent
  const memTone = meterTone(memPct)

  return (
    <div className="mt-3 flex flex-col gap-3 border-t border-border pt-3 sm:flex-row sm:items-center sm:gap-6">
      {/* CPU */}
      <div className="flex items-center gap-2">
        <span className="text-xs uppercase tracking-[0.04em] text-text-3">CPU</span>
        <span className="tnum text-[13px] font-medium text-text">
          {metrics.cpuPercent.toFixed(0)}%
        </span>
        <Sparkline
          data={cpuHistory}
          width={48}
          height={16}
          aria-label="CPU trend"
          className="shrink-0"
        />
      </div>

      {/* Memory */}
      <div className="flex min-w-0 flex-1 items-center gap-2">
        <span className="text-xs uppercase tracking-[0.04em] text-text-3">RAM</span>
        <span className="tnum whitespace-nowrap text-[13px] text-text-2">
          {formatBytes(metrics.memUsedBytes)}
          {metrics.memLimitBytes != null && (
            <>
              {' / '}
              {formatBytes(metrics.memLimitBytes)}
            </>
          )}
          {memPct != null && (
            <span className="ml-1 text-text-3">({memPct.toFixed(0)}%)</span>
          )}
        </span>
        {memPct != null && (
          <Meter
            value={memPct}
            tone={memTone}
            aria-label="Memory usage"
            className="max-w-[120px]"
          />
        )}
      </div>
    </div>
  )
}

// ── Image updates ─────────────────────────────────────────────────────────────

function ImageUpdatesPanel({
  stack,
  onChecked,
}: {
  stack: Stack
  onChecked: (updated: Stack) => void
}) {
  const check = useMutation({
    mutationFn: () => api.stacks.checkUpdates(stack.id),
    onSuccess: (updated) => {
      onChecked(updated)
      toast.success(
        updated.hasUpdates ? 'Updates available.' : 'All images up to date.',
      )
    },
    onError: (err: Error) => toast.error('Update check failed', err.message),
  })

  const checkedAt = stack.updatesCheckedAt

  return (
    <>
      <SectionHeader
        title="Image updates"
        action={
          <Button
            variant="secondary"
            size="sm"
            loading={check.isPending}
            onClick={() => check.mutate()}
          >
            {!check.isPending && <RefreshCw />}
            Check now
          </Button>
        }
      />
      <Card>
        <CardContent className="pt-4 md:pt-5">
          {stack.hasUpdates == null && (
            <p className="text-sm text-text-2">Never checked for updates.</p>
          )}

          {stack.hasUpdates === false && (
            <div className="flex items-center gap-2">
              <CheckCircle2 className="size-4 shrink-0 text-ok" aria-hidden />
              <span className="text-sm text-text-2">All images up to date</span>
              {checkedAt && (
                <span className="tnum text-xs text-text-3" title={absoluteTitle(checkedAt)}>
                  · checked {timeAgo(checkedAt)}
                </span>
              )}
            </div>
          )}

          {stack.hasUpdates === true && (
            <div className="space-y-2">
              <div className="flex items-center gap-2 text-sm font-medium text-warn">
                Updates available
                {checkedAt && (
                  <span
                    className="tnum text-xs font-normal text-text-3"
                    title={absoluteTitle(checkedAt)}
                  >
                    · checked {timeAgo(checkedAt)}
                  </span>
                )}
              </div>
              <ul className="space-y-1.5">
                {(stack.outdatedImages ?? []).map((img) => (
                  <li
                    key={img}
                    className="flex items-center gap-2 rounded-md border border-warn-bd bg-warn-bg px-3 py-2 font-mono text-[12.5px] text-text"
                  >
                    <span className="size-1.5 shrink-0 rounded-full bg-warn" aria-hidden />
                    {img}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </CardContent>
      </Card>
    </>
  )
}

// ── Webhook ───────────────────────────────────────────────────────────────────

function WebhookCard({ stackId, token }: { stackId: number; token: string | null }) {
  const url = webhookUrl(stackId)
  const curl = token
    ? `curl -X POST -H "Authorization: Bearer ${token}" \\\n  ${url}`
    : `curl -X POST ${url}`

  return (
    <Card>
      <CardContent className="space-y-4 pt-4 md:pt-5">
        {!token && (
          <Banner tone="warn" title="No token set">
            This webhook is public and can be triggered without authentication.
          </Banner>
        )}

        <div className="space-y-1.5">
          <p className="text-xs uppercase tracking-[0.04em] text-text-3">URL</p>
          <div className="flex items-center gap-2">
            <code className="min-w-0 flex-1 truncate rounded-md border border-border-strong bg-surface-2 px-3 py-2 font-mono text-[12.5px] text-text-2">
              {url}
            </code>
            <CopyButton value={url} />
          </div>
        </div>

        {token && (
          <div className="space-y-1.5">
            <p className="text-xs uppercase tracking-[0.04em] text-text-3">Token</p>
            <SecretField value={token} readOnly aria-label="Webhook token" />
          </div>
        )}

        <div className="space-y-1.5">
          <p className="text-xs uppercase tracking-[0.04em] text-text-3">Example</p>
          <div className="flex items-start gap-2">
            <pre className="min-w-0 flex-1 overflow-x-auto whitespace-pre rounded-md border border-border-strong bg-surface-2 px-3.5 py-3 font-mono text-[12.5px] text-text-2">
              {curl}
            </pre>
            <CopyButton value={curl} />
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

// ── Deploy history row ──────────────────────────────────────────────────────────

function DeployEventRow({
  event,
  register,
}: {
  event: DeployEvent
  register: (
    eventId: number,
    controls: { expand: () => void; scrollTo: () => void },
  ) => () => void
}) {
  const [expanded, setExpanded] = useState(false)

  // Register expand/scroll controls so the failure banner's "View log" can drive this row.
  // Ref-callback cleanup (React 19): always returns a cleanup that unregisters.
  const setNode = useCallback(
    (node: HTMLDivElement | null) => {
      const unregister = node
        ? register(event.id, {
            expand: () => setExpanded(true),
            scrollTo: () => node.scrollIntoView({ behavior: 'smooth', block: 'center' }),
          })
        : undefined
      return () => {
        unregister?.()
      }
    },
    [event.id, register],
  )

  const isActive = event.status === 'running' || event.status === 'queued'

  return (
    <div ref={setNode} className="overflow-hidden rounded-lg border border-border bg-surface">
      <button
        type="button"
        onClick={() => setExpanded((e) => !e)}
        aria-expanded={expanded}
        className={cn(
          'flex w-full flex-wrap items-center gap-x-3 gap-y-1.5 px-4 py-3 text-left',
          'transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
        )}
      >
        {expanded ? (
          <ChevronDown className="size-4 shrink-0 text-text-3" aria-hidden />
        ) : (
          <ChevronRight className="size-4 shrink-0 text-text-3" aria-hidden />
        )}
        <StatusBadge status={event.status} size="sm" />
        {event.triggeredBy === 'volume-recreate' ? (
          // Data-wipe deploys read distinctly in history (§3.3): a warn-toned trigger chip.
          <Badge tone="warn" size="sm">
            volume-recreate
          </Badge>
        ) : (
          <span className="rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] font-medium text-text-2">
            {event.triggeredBy}
          </span>
        )}
        <span className="tnum text-xs text-text-2" title={absoluteTitle(event.startedAt)}>
          {timeAgo(event.startedAt)}
        </span>
        <span className="tnum ml-auto text-xs text-text-3">
          {formatDuration(event.startedAt, event.finishedAt)}
        </span>
        {isActive && (
          <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold text-run">
            <span
              className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]"
              aria-hidden
            />
            live
          </span>
        )}
      </button>

      {expanded && (
        <div className="border-t border-border p-3">
          <LiveLog
            url={`${apiBase}/api/stacks/events/${event.id}/stream`}
            active={expanded}
            doneEvent="done"
            label={`deploy ${event.id}`}
            maxHeight="18rem"
          />
        </div>
      )}
    </div>
  )
}

// ── Volumes tab (§3.2–§3.5, F4) ─────────────────────────────────────────────────

/** Small StatusBadge-dot + name chip for a container that references a volume. */
function UsedByChip({ name }: { name: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] text-text-2">
      <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
      <span className="truncate font-mono">{name}</span>
    </span>
  )
}

function VolumesTab({ stack, isDeploying }: { stack: Stack; isDeploying: boolean }) {
  const qc = useQueryClient()
  const project = stack.composeProjectName

  const {
    data: volumes = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['volumes', project],
    queryFn: () => api.volumes.list(project),
    // Container backoff (A7): 10s while a deploy is live, else 30s.
    refetchInterval: isDeploying ? 10_000 : 30_000,
  })

  // Lazy sizes (§3.5): fetched once on demand, merged into rows. Never polled.
  const [sizes, setSizes] = useState<Map<string, number> | null>(null)
  const [sizesAt, setSizesAt] = useState<string | null>(null)
  const loadSizes = useMutation({
    mutationFn: () => api.volumes.sizes(project),
    onSuccess: (result: VolumeSize[]) => {
      const map = new Map<string, number>()
      for (const s of result) map.set(s.name, s.sizeBytes)
      setSizes(map)
      setSizesAt(new Date().toISOString())
    },
    onError: (err: Error) =>
      toast({
        tone: 'error',
        title: "Couldn't read volume sizes",
        description: err.message,
        action: { label: 'Retry', onClick: () => loadSizes.mutate() },
      }),
  })

  // Recreate flow state.
  const [recreateOpen, setRecreateOpen] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirmOpen, setConfirmOpen] = useState(false)

  const recreate = useMutation({
    mutationFn: (volumeNames: string[]) => api.volumes.recreate(stack.id, volumeNames),
    onSuccess: () => {
      // The recreate enqueues on the deploy pipeline (§3.3): the deploy banner + a
      // volume-recreate history row take over. Stay on the Volumes tab; refresh stack + events.
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stack.id, 'events'] })
      toast.info(`Recreating volumes for ${stack.name}…`)
    },
    onError: (err: Error) => toast.error('Recreate failed', err.message),
    onSettled: () => {
      setConfirmOpen(false)
      setRecreateOpen(false)
      setSelected(new Set())
    },
  })

  const openRecreate = useCallback((preselect?: string) => {
    setSelected(preselect ? new Set([preselect]) : new Set())
    setConfirmOpen(false)
    setRecreateOpen(true)
  }, [])

  const toggle = useCallback((name: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  }, [])

  const selectedNames = useMemo(() => [...selected], [selected])

  // While a deploy is active, affected rows show a "● live" chip (§3.3 / §6).
  const columns: DataListColumn<VolumeInfo>[] = [
    {
      key: 'name',
      header: 'Volume',
      cell: (v) => (
        <div className="flex items-center gap-2">
          <span className="truncate font-mono text-[12.5px] text-text" title={v.mountpoint}>
            {v.name}
          </span>
          {isDeploying && <LiveChip />}
        </div>
      ),
    },
    {
      key: 'compose',
      header: 'Compose name',
      cell: (v) => (
        <div className="flex items-center gap-1.5">
          {v.composeVolume ? (
            <Badge tone="neutral" size="sm">
              {v.composeVolume}
            </Badge>
          ) : (
            <span className="text-text-3">—</span>
          )}
          {v.driver !== 'local' && (
            <Badge tone="neutral" size="sm">
              {v.driver}
            </Badge>
          )}
        </div>
      ),
    },
    {
      key: 'lifecycle',
      header: 'Status',
      cell: (v) => <LifecycleBadge lifecycle={v.lifecycle} />,
    },
    {
      key: 'usedBy',
      header: 'Used by',
      cell: (v) =>
        v.inUseBy.length === 0 ? (
          <span className="text-text-3">—</span>
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {v.inUseBy.map((n) => (
              <UsedByChip key={n} name={n} />
            ))}
          </div>
        ),
    },
    {
      key: 'size',
      header: 'Size',
      align: 'right',
      cell: (v) => (
        <span className="tnum text-[13px] text-text-2">
          {sizes ? formatBytes(sizes.get(v.name) ?? 0) : '—'}
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (v) => (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${v.name}`}>
              <MoreHorizontal />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent>
            <DropdownMenuItem destructive onSelect={() => openRecreate(v.name)}>
              <RotateCcw /> Recreate…
            </DropdownMenuItem>
            <DropdownMenuItem
              onSelect={() => {
                void navigator.clipboard?.writeText(v.mountpoint)
                toast.success('Copied to clipboard.')
              }}
            >
              <Copy /> Copy mountpoint
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      ),
    },
  ]

  if (isError) {
    return (
      <Banner
        tone="danger"
        title="Couldn’t load volumes"
        action={
          <Button variant="secondary" size="sm" onClick={() => refetch()}>
            Retry
          </Button>
        }
      >
        The Docker socket may be unreachable.
      </Banner>
    )
  }

  return (
    <div className="space-y-4">
      <SectionHeader
        title="Volumes"
        action={
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              size="sm"
              loading={loadSizes.isPending}
              onClick={() => loadSizes.mutate()}
            >
              {!loadSizes.isPending && <HardDrive />}
              {sizes ? 'Refresh sizes' : 'Load sizes'}
            </Button>
            <Button variant="secondary" size="sm" onClick={() => openRecreate()}>
              <RotateCcw /> Recreate volume…
            </Button>
          </div>
        }
      />
      {/* Plain-language lead-in (F10). */}
      <p className="-mt-2 text-[13px] text-text-2">
        Volumes hold this stack’s persistent data — they survive deploys until you recreate them.
      </p>
      {sizesAt && (
        <p className="tnum text-xs text-text-3" title={absoluteTitle(sizesAt)}>
          Sizes as of {new Date(sizesAt).toLocaleTimeString()}
        </p>
      )}

      <DataList
        items={volumes}
        columns={columns}
        getKey={(v) => v.name}
        skeletonRows={isLoading ? 4 : undefined}
        aria-label="Volumes"
        emptyState={
          <EmptyState
            icon={Database}
            title="No volumes"
            description="This stack’s compose file declares no named volumes."
          />
        }
        renderCard={(v) => (
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-2">
              <span className="min-w-0 truncate font-mono text-[13px] text-text">{v.name}</span>
              <div className="flex items-center gap-2">
                {isDeploying && <LiveChip />}
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${v.name}`}>
                      <MoreHorizontal />
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent>
                    <DropdownMenuItem destructive onSelect={() => openRecreate(v.name)}>
                      <RotateCcw /> Recreate…
                    </DropdownMenuItem>
                    <DropdownMenuItem
                      onSelect={() => {
                        void navigator.clipboard?.writeText(v.mountpoint)
                        toast.success('Copied to clipboard.')
                      }}
                    >
                      <Copy /> Copy mountpoint
                    </DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
              </div>
            </div>
            <div className="flex flex-wrap items-center gap-1.5 text-[12px] text-text-2">
              {v.composeVolume && (
                <Badge tone="neutral" size="sm">
                  {v.composeVolume}
                </Badge>
              )}
              <span>· {v.driver}</span>
              <LifecycleBadge lifecycle={v.lifecycle} />
            </div>
            {v.inUseBy.length > 0 && (
              <div className="flex flex-wrap gap-1.5">
                {v.inUseBy.map((n) => (
                  <UsedByChip key={n} name={n} />
                ))}
              </div>
            )}
            <p className="tnum text-[12px] text-text-3">
              Size {sizes ? formatBytes(sizes.get(v.name) ?? 0) : '—'}
            </p>
          </div>
        )}
      />

      {/* Step 1 — select volumes to recreate. */}
      <RecreateSelectDialog
        open={recreateOpen}
        onOpenChange={(o) => {
          setRecreateOpen(o)
          if (!o) setSelected(new Set())
        }}
        stack={stack}
        volumes={volumes}
        sizes={sizes}
        selected={selected}
        onToggle={toggle}
        onContinue={() => {
          setRecreateOpen(false)
          setConfirmOpen(true)
        }}
      />

      {/* Step 2 — typed-name confirm (A4), tone danger, "Wipe & redeploy". */}
      <ConfirmDialog
        open={confirmOpen}
        onOpenChange={(o) => {
          setConfirmOpen(o)
          if (!o) {
            // Backing out of the confirm returns to the selection dialog.
            setRecreateOpen(true)
          }
        }}
        title={`Wipe data for ${stack.name}?`}
        description={
          <span>
            This permanently deletes {selectedNames.length} volume(s) and all their data —
            including any database contents — then redeploys the stack to recreate them empty.{' '}
            <strong>This cannot be undone.</strong>
            <span className="mt-2 flex flex-col gap-0.5">
              {selectedNames.map((n) => (
                <span key={n} className="font-mono text-[12px] text-text">
                  {n}
                </span>
              ))}
            </span>
          </span>
        }
        confirmLabel="Wipe & redeploy"
        tone="danger"
        requireText={stack.name}
        loading={recreate.isPending}
        onConfirm={() => recreate.mutate(selectedNames)}
      />
    </div>
  )
}

/** Step 1 of the recreate flow: a checkbox list of the stack's named volumes (§3.3). */
function RecreateSelectDialog({
  open,
  onOpenChange,
  stack,
  volumes,
  sizes,
  selected,
  onToggle,
  onContinue,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  stack: Stack
  volumes: VolumeInfo[]
  sizes: Map<string, number> | null
  selected: Set<string>
  onToggle: (name: string) => void
  onContinue: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Recreate volumes for {stack.name}</DialogTitle>
          <DialogDescription>
            Choose which named volumes to wipe and recreate empty on the next deploy.
          </DialogDescription>
        </DialogHeader>

        <Banner tone="danger" title="Recreating deletes data permanently">
          Watchtower will stop this stack’s containers, delete the selected volumes, then redeploy
          to recreate them empty. This is how you reset a database to a clean state.
        </Banner>

        <div className="flex max-h-[40dvh] flex-col gap-1 overflow-y-auto">
          {volumes.length === 0 ? (
            <p className="text-sm text-text-3">This stack has no named volumes.</p>
          ) : (
            volumes.map((v) => (
              <label
                key={v.name}
                className="flex cursor-pointer items-center gap-3 rounded-md border border-border px-3 py-2 hover:bg-surface-2"
              >
                <input
                  type="checkbox"
                  checked={selected.has(v.name)}
                  onChange={() => onToggle(v.name)}
                  className="size-4 shrink-0 accent-[var(--brand)]"
                />
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-mono text-[12.5px] text-text">{v.name}</span>
                  <span className="block text-[12px] text-text-2">
                    {v.composeVolume ?? '—'}
                    {v.inUseBy.length > 0 && ` · used by ${v.inUseBy.join(', ')}`}
                    {sizes && ` · ${formatBytes(sizes.get(v.name) ?? 0)}`}
                  </span>
                </span>
              </label>
            ))
          )}
        </div>

        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button variant="danger" disabled={selected.size === 0} onClick={onContinue}>
            Continue
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// ── Networks tab (§4.2–§4.3, F10) ────────────────────────────────────────────────

function NetworksTab({ stack }: { stack: Stack }) {
  const project = stack.composeProjectName

  const {
    data: networks = [],
    isLoading: netsLoading,
    isError: netsError,
    refetch: refetchNets,
  } = useQuery({
    queryKey: ['networks', project],
    queryFn: () => api.networks.list(project),
    refetchInterval: 30_000,
  })

  const {
    data: ports,
    isLoading: portsLoading,
  } = useQuery({
    queryKey: ['networks', 'ports', project],
    queryFn: () => api.networks.ports(project),
    refetchInterval: 30_000,
  })

  const published = useMemo(() => {
    const list = ports?.published ?? []
    // Sort by exposure risk: public first, then localhost, then internal-only.
    const rank: Record<string, number> = { public: 0, localhost: 1, none: 2 }
    return [...list].sort((a, b) => (rank[a.exposure] ?? 3) - (rank[b.exposure] ?? 3))
  }, [ports])
  const conflicts = ports?.conflicts ?? []

  if (netsError) {
    return (
      <Banner
        tone="danger"
        title="Couldn’t load networks"
        action={
          <Button variant="secondary" size="sm" onClick={() => refetchNets()}>
            Retry
          </Button>
        }
      >
        The Docker socket may be unreachable.
      </Banner>
    )
  }

  const netColumns: DataListColumn<NetworkInfo>[] = [
    {
      key: 'name',
      header: 'Network',
      cell: (n) => <span className="truncate font-mono text-[12.5px] text-text">{n.name}</span>,
    },
    {
      key: 'compose',
      header: 'Compose name',
      cell: (n) =>
        n.composeNetwork ? (
          <Badge tone="neutral" size="sm">
            {n.composeNetwork}
          </Badge>
        ) : (
          <span className="text-text-3">—</span>
        ),
    },
    {
      key: 'driver',
      header: 'Driver',
      cell: (n) => (
        <div className="flex flex-wrap items-center gap-1.5">
          <Badge tone="neutral" size="sm">
            {n.driver}
          </Badge>
          {n.internal && (
            <Tooltip label="Internal network — no outbound route.">
              <Badge tone="warn" size="sm" tabIndex={0}>
                internal
              </Badge>
            </Tooltip>
          )}
        </div>
      ),
    },
    {
      key: 'ipam',
      header: 'Subnet',
      cell: (n) => (
        <span className="tnum font-mono text-[12px] text-text-2">
          {n.ipam.subnet ?? '—'}
          {n.ipam.gateway && <span className="text-text-3"> · gw {n.ipam.gateway}</span>}
        </span>
      ),
    },
    {
      key: 'attached',
      header: 'Attached',
      cell: (n) =>
        n.attached.length === 0 ? (
          <span className="text-text-3">—</span>
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {n.attached.map((e) => (
              <span
                key={e.containerId}
                className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] text-text-2"
              >
                <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
                <span className="truncate font-mono">{e.containerName}</span>
                {e.ipv4 && <span className="tnum text-text-3">· {e.ipv4}</span>}
              </span>
            ))}
          </div>
        ),
    },
  ]

  return (
    <div className="space-y-8">
      {/* Block A — networks + attachment strip. */}
      <section className="space-y-4">
        <SectionHeader title="Networks" />
        <p className="-mt-2 text-[13px] text-text-2">
          How this stack’s services are wired together on the Docker network.
        </p>
        <DataList
          items={networks}
          columns={netColumns}
          getKey={(n) => n.id}
          skeletonRows={netsLoading ? 2 : undefined}
          aria-label="Networks"
          emptyState={
            <EmptyState
              icon={Network}
              title="No networks"
              description="This stack has no dedicated networks."
            />
          }
          renderCard={(n) => (
            <div className="space-y-2">
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="min-w-0 truncate font-mono text-[13px] text-text">{n.name}</span>
                <Badge tone="neutral" size="sm">
                  {n.driver}
                </Badge>
                {n.internal && (
                  <Badge tone="warn" size="sm">
                    internal
                  </Badge>
                )}
              </div>
              {n.ipam.subnet && (
                <p className="tnum font-mono text-[12px] text-text-2">{n.ipam.subnet}</p>
              )}
              {n.attached.length > 0 && (
                <div className="flex flex-wrap gap-1.5">
                  {n.attached.map((e) => (
                    <span
                      key={e.containerId}
                      className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] text-text-2"
                    >
                      <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
                      <span className="truncate font-mono">{e.containerName}</span>
                      {e.ipv4 && <span className="tnum text-text-3">· {e.ipv4}</span>}
                    </span>
                  ))}
                </div>
              )}
            </div>
          )}
        />

        {networks.length > 0 && <AttachmentStrip networks={networks} />}
      </section>

      {/* Block B — published ports exposure map. */}
      <section className="space-y-4">
        <SectionHeader title="Published ports" />
        <p className="-mt-2 text-[13px] text-text-2">
          What deploying this stack opened to the network.
        </p>

        {conflicts.map((c) => (
          <PortConflictBanner key={`${c.hostIp}:${c.publicPort}/${c.protocol}`} conflict={c} />
        ))}

        {portsLoading ? (
          <div className="space-y-2 rounded-lg border border-border p-4">
            {Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} variant="line" className="h-4 w-2/3" />
            ))}
          </div>
        ) : published.length === 0 ? (
          <Banner tone="info" title="No published ports">
            This stack isn’t reachable from the host network.
          </Banner>
        ) : (
          <ExposureTable ports={published} />
        )}
      </section>
    </div>
  )
}

function PortConflictBanner({ conflict }: { conflict: PortConflict }) {
  return (
    <Banner tone="warn" title="Port conflict">
      Port {conflict.publicPort}/{conflict.protocol} is claimed by {conflict.containerNames.length}{' '}
      containers ({conflict.containerNames.join(', ')}).
    </Banner>
  )
}

function ExposureTable({ ports }: { ports: PublishedPort[] }) {
  const columns: DataListColumn<PublishedPort>[] = [
    {
      key: 'container',
      header: 'Container',
      cell: (p) => <span className="truncate font-mono text-[12.5px] text-text">{p.containerName}</span>,
    },
    {
      key: 'port',
      header: 'Port',
      cell: (p) => (
        <span className="tnum font-mono text-[12.5px] text-text-2">
          {p.privatePort}/{p.protocol}
        </span>
      ),
    },
    {
      key: 'binding',
      header: 'Host binding',
      cell: (p) => (
        <span className="tnum font-mono text-[12px] text-text-2">
          {p.publicPort != null ? `${p.hostIp}:${p.publicPort}` : '—'}
        </span>
      ),
    },
    {
      key: 'exposure',
      header: 'Exposure',
      align: 'right',
      cell: (p) => <ExposureBadge exposure={p.exposure} />,
    },
  ]

  return (
    <DataList
      items={ports}
      columns={columns}
      getKey={(p) => `${p.containerId}:${p.privatePort}/${p.protocol}:${p.hostIp}:${p.publicPort}`}
      aria-label="Published ports"
      renderCard={(p) => (
        <div className="flex items-center justify-between gap-3">
          <div className="min-w-0">
            <p className="truncate font-mono text-[13px] text-text">{p.containerName}</p>
            <p className="tnum font-mono text-[12px] text-text-2">
              {p.privatePort}/{p.protocol}
              {p.publicPort != null && ` · ${p.hostIp}:${p.publicPort}`}
            </p>
          </div>
          <ExposureBadge exposure={p.exposure} />
        </div>
      )}
    />
  )
}

/**
 * Lightweight topology (§4.3): one row per network, containers as dots on a rail linking to a
 * central network pill. Internal networks get a lock glyph; the default bridge is de-emphasized.
 * Collapses to a grouped list on mobile.
 */
function AttachmentStrip({ networks }: { networks: NetworkInfo[] }) {
  const withMembers = networks.filter((n) => n.attached.length > 0)
  if (withMembers.length === 0) return null

  return (
    <div className="space-y-3">
      {withMembers.map((n) => (
        <div
          key={n.id}
          className={cn(
            'flex flex-col gap-3 rounded-lg border border-border bg-surface p-4 md:flex-row md:items-center md:gap-4',
            n.isDefault && 'opacity-80',
          )}
        >
          {/* Network pill */}
          <span
            className={cn(
              'inline-flex w-fit items-center gap-1.5 rounded-full border px-3 py-1 text-[12px] font-medium',
              n.isDefault
                ? 'border-border bg-surface-2 text-text-3'
                : 'border-[var(--brand-soft)] bg-brand-soft text-brand',
            )}
          >
            {n.internal && <Lock className="size-3" aria-hidden />}
            <span className="font-mono">{n.name}</span>
            <span className="text-text-3">({n.driver})</span>
          </span>

          {/* Rail of container dots */}
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 md:border-l md:border-border md:pl-4">
            {n.attached.map((e) => (
              <span key={e.containerId} className="inline-flex items-center gap-1.5 text-[12px] text-text-2">
                <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
                <span className="font-mono">{e.containerName}</span>
                {e.ipv4 && <span className="tnum text-text-3">{e.ipv4}</span>}
              </span>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

// ── Settings tab ────────────────────────────────────────────────────────────────

function SettingsTab({
  stackId,
  stack,
  envVars,
  credentials,
}: {
  stackId: number
  stack: Stack
  envVars: StackEnvVar[]
  credentials: Credential[]
}) {
  const qc = useQueryClient()
  const navigate = useNavigate()

  const [form, setForm] = useState<Omit<UpdateStackRequest, 'envVars'>>({
    name: stack.name,
    repositoryUrl: stack.repositoryUrl,
    composeFilePath: stack.composeFilePath,
    branch: stack.branch,
    composeProjectName: stack.composeProjectName,
    credentialId: stack.credentialId,
    webhookToken: stack.webhookToken ?? '',
    webhookEnabled: stack.webhookEnabled,
  })

  const [envDraft, setEnvDraft] = useState<StackEnvVarInput[]>([
    ...envVars.map((v) => ({ key: v.key, value: v.value })),
    { key: '', value: '' },
  ])

  const [confirmDelete, setConfirmDelete] = useState(false)

  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) =>
    setForm((prev) => ({ ...prev, [key]: value }))

  const update = useMutation({
    mutationFn: (data: UpdateStackRequest) => api.stacks.update(stackId, data),
    onSuccess: (updated) => {
      qc.setQueryData(['stacks', stackId], updated)
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'env'] })
      qc.invalidateQueries({ queryKey: ['stacks'] })
      toast.success('Settings saved.')
    },
    onError: (err: Error) => toast.error('Save failed', err.message),
  })

  const deleteStack = useMutation({
    mutationFn: () => api.stacks.delete(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      toast.success(`Deleted ${stack.name}.`)
      navigate({ to: '/stacks' })
    },
    onError: (err: Error) => toast.error('Delete failed', err.message),
    onSettled: () => setConfirmDelete(false),
  })

  function handleSave(e: React.FormEvent) {
    e.preventDefault()
    const validEnv = envDraft.filter((v) => v.key.trim() !== '')
    update.mutate({
      ...form,
      composeProjectName: form.composeProjectName || null,
      webhookToken: form.webhookToken || null,
      envVars: validEnv,
    })
  }

  const url = webhookUrl(stackId)
  const curlHint = form.webhookToken
    ? `curl -X POST -H "Authorization: Bearer <token>" ${url}`
    : `curl -X POST ${url}`

  return (
    <form onSubmit={handleSave} className="max-w-2xl space-y-8">
      {/* Configuration */}
      <section>
        <SectionHeader
          title="Configuration"
          description="Where the compose project lives and how it’s deployed."
        />
        <Card>
          <CardContent className="grid grid-cols-1 gap-4 pt-4 md:grid-cols-2 md:pt-5">
            <Field label="Stack name" required className="md:col-span-2">
              {({ id }) => (
                <Input
                  id={id}
                  mono
                  value={form.name}
                  onChange={(e) => set('name', e.target.value)}
                  required
                />
              )}
            </Field>

            <Field label="Repository URL" required className="md:col-span-2">
              {({ id }) => (
                <Input
                  id={id}
                  mono
                  value={form.repositoryUrl}
                  onChange={(e) => set('repositoryUrl', e.target.value)}
                  placeholder="https://github.com/owner/repo"
                  required
                />
              )}
            </Field>

            <Field
              label="Branch"
              hint="Defaults to main"
            >
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  mono
                  value={form.branch}
                  onChange={(e) => set('branch', e.target.value)}
                  placeholder="main"
                />
              )}
            </Field>

            <Field
              label="Compose file path"
              hint="Relative to the repo root, e.g. docker-compose.yml"
            >
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  mono
                  value={form.composeFilePath}
                  onChange={(e) => set('composeFilePath', e.target.value)}
                  placeholder="docker-compose.yml"
                />
              )}
            </Field>

            <Field
              label="Compose project name"
              hint="Defaults to the stack name"
              className="md:col-span-2"
            >
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  mono
                  value={form.composeProjectName ?? ''}
                  onChange={(e) => set('composeProjectName', e.target.value)}
                />
              )}
            </Field>
          </CardContent>
        </Card>
      </section>

      {/* Authentication */}
      <section>
        <SectionHeader
          title="Authentication"
          description="Only needed for private repos or registries."
        />
        <Card>
          <CardContent className="space-y-4 pt-4 md:pt-5">
            <Field label="Credential" hint="Only needed for private repositories">
              <Select
                value={form.credentialId != null ? String(form.credentialId) : NO_CREDENTIAL}
                onValueChange={(v) => set('credentialId', v === NO_CREDENTIAL ? null : Number(v))}
              >
                <SelectTrigger>
                  <SelectValue placeholder="None (public repository)" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={NO_CREDENTIAL}>None (public repository)</SelectItem>
                  {credentials.map((c) => (
                    <SelectItem key={c.id} value={String(c.id)}>
                      {c.name} ({c.username})
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>

            <Field label="Webhook">
              <label className="flex items-center gap-3">
                <Switch
                  checked={form.webhookEnabled ?? false}
                  onCheckedChange={(v) => set('webhookEnabled', v)}
                />
                <span className="text-sm text-text">Enable webhook endpoint</span>
              </label>
            </Field>

            {form.webhookEnabled && (
              <>
                {!form.webhookToken && (
                  <Banner tone="warn" title="No token set">
                    This webhook is public and can be triggered without authentication.
                  </Banner>
                )}

                <Field
                  label="Webhook token"
                  hint="Sent as a Bearer token by your CI. Leave blank to allow unauthenticated deploys (not recommended)."
                >
                  <SecretField
                    value={form.webhookToken ?? ''}
                    onChange={(v) => set('webhookToken', v)}
                    aria-label="Webhook token"
                  />
                </Field>

                <Field label="Webhook URL">
                  <div className="flex items-center gap-2">
                    <Input mono readOnly value={url} aria-label="Webhook URL" />
                    <CopyButton value={url} />
                  </div>
                </Field>

                <p className="font-mono text-[12px] text-text-3">{curlHint}</p>
              </>
            )}
          </CardContent>
        </Card>
      </section>

      {/* Environment variables */}
      <section>
        <SectionHeader
          title="Environment variables"
          description="Injected via --env-file on every deploy. Reference them as ${KEY} in your compose file."
        />
        <EnvVarEditor value={envDraft} onChange={setEnvDraft} />
      </section>

      {/* Save */}
      <div className="flex items-center gap-3">
        <Button type="submit" variant="primary" loading={update.isPending}>
          Save settings
        </Button>
      </div>

      {/* Danger zone */}
      <section>
        <SectionHeader title="Danger zone" />
        <Card className="border-danger-bd">
          <CardContent className="flex flex-col gap-4 pt-4 sm:flex-row sm:items-center sm:justify-between md:pt-5">
            <div className="min-w-0">
              <p className="text-sm font-medium text-text">Delete stack</p>
              <p className="mt-0.5 text-[13px] text-text-2">
                This permanently deletes the stack and its deployment history. Running containers
                are not affected.
              </p>
            </div>
            <Button
              type="button"
              variant="danger"
              className="shrink-0"
              onClick={() => setConfirmDelete(true)}
            >
              Delete stack
            </Button>
          </CardContent>
        </Card>
      </section>

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete ${stack.name}?`}
        description="This permanently deletes the stack and its deployment history. Running containers are not affected."
        confirmLabel="Delete stack"
        tone="danger"
        requireText={stack.name}
        loading={deleteStack.isPending}
        onConfirm={() => deleteStack.mutate()}
      />
    </form>
  )
}

// ── Loading skeleton ─────────────────────────────────────────────────────────────

function StackDetailSkeleton() {
  return (
    <div className="mx-auto max-w-[1200px] space-y-6">
      <Skeleton variant="line" className="h-4 w-32" />
      <div className="space-y-2">
        <Skeleton variant="line" className="h-8 w-56" />
        <Skeleton variant="line" className="h-4 w-80 max-w-full" />
      </div>
      <Skeleton variant="rect" className="h-14 w-full" />
      <Skeleton variant="line" className="h-9 w-48" />
      <div className="space-y-3">
        <Skeleton variant="rect" className="h-40 w-full" />
        <Skeleton variant="rect" className="h-40 w-full" />
      </div>
    </div>
  )
}
