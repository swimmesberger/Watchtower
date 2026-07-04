import { useCallback, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from '@tanstack/react-router'
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
import type {
  Container,
  Credential,
  DeployEvent,
  Stack,
  StackEnvVar,
  StackEnvVarInput,
  UpdateStackRequest,
} from '@/lib/types'
import { absoluteTitle, formatDuration, timeAgo } from '@/lib/format'
import { cn } from '@/lib/utils'
import { ContainerLogs } from '@/components/container-logs'
import { EnvVarEditor } from '@/components/env-var-editor'
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
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { LiveLog } from '@/components/ui/live-log'
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
import { StatusBadge } from '@/components/ui/status-badge'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

const NO_CREDENTIAL = 'none'

function webhookUrl(stackId: number): string {
  const base = apiBase || (typeof window !== 'undefined' ? window.location.origin : '')
  return `${base}/api/webhooks/stacks/${stackId}/deploy`
}

export function StackDetailPage() {
  const { id } = useParams({ from: '/stacks/$id' })
  const stackId = Number(id)
  const qc = useQueryClient()

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

      {/* Tabs */}
      <Tabs defaultValue="overview">
        <TabsList>
          <TabsTrigger value="overview">Overview</TabsTrigger>
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
                    <DeployEventRow key={event.id} event={event} register={registerHistoryRow} />
                  ))}
                </div>
              )}
            </section>
          </div>
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

function ContainerCard({ container }: { container: Container }) {
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
        <span className="rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] font-medium text-text-2">
          {event.triggeredBy}
        </span>
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
