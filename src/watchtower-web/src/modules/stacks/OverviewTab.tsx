import { useCallback, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { type RegisterHistoryRow, useRegisterHistoryRow } from './StackDetailPage'
import {
  Boxes,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  MoreHorizontal,
  Play,
  RefreshCw,
  RotateCcw,
  Square,
  Trash2,
} from 'lucide-react'
import { api } from '@/lib/api'
import { apiBase } from '@/lib/config'
import type { Container, DeployEvent, Stack } from '@/lib/types'
import { absoluteTitle, formatDuration, timeAgo } from '@/lib/format'
import { cn } from '@/lib/utils'
import { containerCardExtras } from '@/platform/points'
import { ContainerLogs } from '@/components/container-logs'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { CopyButton } from '@/components/ui/copy-button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { LiveLog } from '@/components/ui/live-log'
import { SecretField } from '@/components/ui/secret-field'
import { SectionHeader } from '@/components/ui/section-header'
import { StatusBadge } from '@/components/ui/status-badge'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

function webhookUrl(stackId: number): string {
  const base = apiBase || (typeof window !== 'undefined' ? window.location.origin : '')
  return `${base}/api/webhooks/stacks/${stackId}/deploy`
}

export function OverviewTab({ stack }: { stack: Stack }) {
  const qc = useQueryClient()
  const stackId = stack.id

  // The page provides this via context so the failure-banner hero's "View log" can expand +
  // scroll to the latest failed deploy-history row (which now lives in this tab). Defaults to
  // a no-op when rendered without the page provider.
  const register = useRegisterHistoryRow()

  const { data: containers = [] } = useQuery({
    queryKey: ['containers'],
    queryFn: api.containers.list,
    refetchInterval: 10_000,
  })

  const isDeploying =
    stack.lastDeployStatus === 'running' || stack.lastDeployStatus === 'queued'

  const { data: events = [] } = useQuery({
    queryKey: ['stacks', stackId, 'events'],
    queryFn: () => api.stacks.events(stackId),
    refetchInterval: isDeploying ? 3000 : false,
  })

  const deploy = useMutation({
    mutationFn: () => api.stacks.deploy(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'events'] })
      toast.info(`Deploying ${stack.name ?? 'stack'}…`)
    },
    onError: (err: Error) => toast.error('Deploy failed', err.message),
  })

  const stackContainers = containers.filter((c) => c.stackName === stack.composeProjectName)

  return (
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
              <ContainerCard key={container.id} container={container} />
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
              <DeployEventRow key={event.id} event={event} register={register} />
            ))}
          </div>
        )}
      </section>
    </div>
  )
}

// ── Container card ────────────────────────────────────────────────────────────

function ContainerCard({ container }: { container: Container }) {
  const qc = useQueryClient()
  const [confirmRemove, setConfirmRemove] = useState(false)
  const name = container.names[0]?.replace(/^\//, '') ?? container.id.slice(0, 12)

  // Container-metrics (and any other) extras contributed by sibling modules render
  // after the meta grid. Each extra fetches its own data; this card just gives it the container.
  const extras = useContributions(containerCardExtras)

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
        {extras.map((e) => (
          <e.component key={e.id} container={container} />
        ))}
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
      toast.success(updated.hasUpdates ? 'Updates available.' : 'All images up to date.')
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
  register: RegisterHistoryRow
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
