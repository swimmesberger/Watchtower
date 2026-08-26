import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link } from '@tanstack/react-router'
import { Boxes, Play, Plus, Square, Trash2, X } from 'lucide-react'
import { api } from '@/lib/api'
import { deployTargetVersion, usesReleases } from '@/lib/release'
import type { Stack } from '@/lib/types'
import { absoluteTitle, timeAgo } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { StatusBadge } from '@/components/ui/status-badge'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

const stacksApi = getRouteApi('/stacks')

const FILTER_LABEL: Record<'ok' | 'failed', string> = {
  ok: 'Status: healthy',
  failed: 'Status: failed',
}

function matchesFilter(stack: Stack, status: 'ok' | 'failed'): boolean {
  return status === 'ok'
    ? stack.lastDeployStatus === 'success'
    : stack.lastDeployStatus === 'failed'
}

function isDeploying(stack: Stack): boolean {
  return stack.lastDeployStatus === 'running' || stack.lastDeployStatus === 'queued'
}

export function StacksPage() {
  const qc = useQueryClient()
  const { status } = stacksApi.useSearch()
  const navigate = stacksApi.useNavigate()
  // The catalogue page is gated on the Products module, so the product cell only links when it is
  // somewhere the reader can actually land.
  const productsEnabled = stacksApi.useRouteContext().caps.isModuleEnabled('Products')

  const [pendingDelete, setPendingDelete] = useState<Stack | null>(null)

  const {
    data: stacks = [],
    isLoading,
    isError,
    error,
    refetch,
  } = useQuery({
    queryKey: ['stacks'],
    queryFn: api.stacks.list,
    // Poll every 2s while any stack is actively deploying or waiting in queue.
    // The backend eagerly sets Running/Queued status before returning the 202, so the
    // first refetch after a deploy mutation will already see the correct status.
    refetchInterval: (q) => {
      const data = q.state.data ?? []
      return data.some(isDeploying) ? 2000 : false
    },
  })

  const isFastPolling = stacks.some(isDeploying)

  const deploy = useMutation({
    mutationFn: (stack: Stack) => api.stacks.deploy(stack.id),
    onSuccess: (_data, stack) => {
      toast.info(`Deploying ${stack.name}…`)
      qc.invalidateQueries({ queryKey: ['stacks'] })
    },
    onError: (err: Error, stack) => {
      toast({
        tone: 'error',
        title: `Deploy failed for ${stack.name}`,
        description: err.message,
        action: { label: 'Retry', onClick: () => deploy.mutate(stack) },
      })
    },
  })

  const stop = useMutation({
    mutationFn: (stack: Stack) => api.stacks.stop(stack.id),
    onSuccess: (_data, stack) => {
      toast.success(`Stopped ${stack.name}. It stays stopped until you start it again.`)
      qc.invalidateQueries({ queryKey: ['stacks'] })
    },
    onError: (err: Error, stack) => {
      toast.error(`Failed to stop ${stack.name}: ${err.message}`)
    },
  })

  const start = useMutation({
    mutationFn: (stack: Stack) => api.stacks.start(stack.id),
    onSuccess: (data, stack) => {
      if (data.started) toast.success(`Started ${stack.name}.`)
      else toast.info(`${stack.name} is enabled again — deploy it to create its containers.`)
      qc.invalidateQueries({ queryKey: ['stacks'] })
    },
    onError: (err: Error, stack) => {
      toast.error(`Failed to start ${stack.name}: ${err.message}`)
    },
  })

  const remove = useMutation({
    mutationFn: (stack: Stack) => api.stacks.delete(stack.id),
    onSuccess: (_data, stack) => {
      toast.success(`Deleted ${stack.name}.`)
      qc.invalidateQueries({ queryKey: ['stacks'] })
    },
    onError: (err: Error, stack) => {
      toast.error(`Failed to delete ${stack.name}: ${err.message}`)
    },
    onSettled: () => setPendingDelete(null),
  })

  const filtered = status ? stacks.filter((s) => matchesFilter(s, status)) : stacks
  const anyReleaseMode = stacks.some(usesReleases)

  function clearFilter() {
    navigate({ search: {} })
  }

  function DeployButton({ stack }: { stack: Stack }) {
    const pending = deploy.isPending && deploy.variables?.id === stack.id
    return (
      <Button
        size="sm"
        variant="secondary"
        loading={pending}
        disabled={isDeploying(stack) || stack.desiredState === 'stopped'}
        onClick={() => deploy.mutate(stack)}
      >
        <Play /> Deploy
      </Button>
    )
  }

  function StopStartButton({ stack }: { stack: Stack }) {
    const stopped = stack.desiredState === 'stopped'
    const pending =
      (stop.isPending && stop.variables?.id === stack.id) ||
      (start.isPending && start.variables?.id === stack.id)
    return (
      <Tooltip
        label={
          stopped
            ? 'Start the stack’s containers'
            : 'Stop all containers; the stack stays stopped until started again'
        }
      >
        <Button
          size="sm"
          variant="secondary"
          loading={pending}
          disabled={isDeploying(stack)}
          onClick={() => (stopped ? start.mutate(stack) : stop.mutate(stack))}
        >
          {stopped ? (
            <>
              <Play /> Start
            </>
          ) : (
            <>
              <Square /> Stop
            </>
          )}
        </Button>
      </Tooltip>
    )
  }

  /**
   * Invariant 6 on a list: the cell beside each row's Deploy button states what that button would
   * apply. In `Git` mode that is the branch, exactly as this column has always shown. In `Releases`
   * mode the branch is meaningless — the deploy checks out the release's commit — so the chip
   * becomes the version, with `pinned` beside it when the row is not tracking latest.
   *
   * Derived from the list DTO alone (`pin ?? availableReleaseVersion ?? lastDeployedRelease`), so the
   * page still makes exactly one request. Null only for a Releases-mode product with no releases at
   * all; the row then shows nothing rather than a made-up value.
   */
  function VersionCell({ stack }: { stack: Stack }) {
    if (!usesReleases(stack)) return <Badge tone="neutral">{stack.branch}</Badge>
    const target = deployTargetVersion(stack)
    if (!target) return <span className="text-text-3">—</span>
    return (
      <span className="inline-flex items-center gap-1.5">
        <Badge tone="neutral">{target}</Badge>
        {stack.pinnedRelease && (
          <Badge tone="neutral" size="sm">
            pinned
          </Badge>
        )}
      </span>
    )
  }

  /** The product a stack runs, linked only when the catalogue page is actually reachable. */
  function ProductCell({ stack }: { stack: Stack }) {
    const className = 'block max-w-[20ch] truncate text-[13px] text-text-2'
    if (!productsEnabled) {
      return (
        <span className={className} title={stack.repositoryUrl}>
          {stack.productName}
        </span>
      )
    }
    return (
      <Link
        to="/products/$id"
        params={{ id: String(stack.productId) }}
        className={cn(className, 'hover:text-brand')}
        title={stack.repositoryUrl}
      >
        {stack.productName}
      </Link>
    )
  }

  function DeleteButton({ stack }: { stack: Stack }) {
    return (
      <Tooltip label="Delete stack">
        <Button
          size="icon-sm"
          variant="ghost"
          aria-label={`Delete ${stack.name}`}
          onClick={() => setPendingDelete(stack)}
          className="text-text-2 hover:text-danger"
        >
          <Trash2 />
        </Button>
      </Tooltip>
    )
  }

  const columns: DataListColumn<Stack>[] = [
    {
      key: 'name',
      header: 'Name',
      cell: (s) => (
        <Link
          to="/stacks/$id"
          params={{ id: String(s.id) }}
          className="inline-flex items-center gap-2 font-medium text-text hover:text-brand"
        >
          <StatusDot status={s.lastDeployStatus} />
          {s.name}
        </Link>
      ),
    },
    {
      key: 'product',
      header: 'Product',
      // Replaces the Repository column rather than joining it: the product carries the identity and
      // the repository URL rides along as its tooltip, keeping this list at the width it had.
      cell: (s) => <ProductCell stack={s} />,
    },
    {
      key: 'branch',
      // "Branch" while every product on this box is in Git mode — an install that never opts into
      // releases sees this page exactly as it always was (design.md §Migration morning-after). The
      // moment one release-mode stack is listed the column is answering a broader question, and says
      // so; a Git row's branch is still the version it deploys.
      header: anyReleaseMode ? 'Version' : 'Branch',
      cell: (s) => <VersionCell stack={s} />,
    },
    {
      key: 'status',
      header: 'Status',
      cell: (s) =>
        s.desiredState === 'stopped' ? (
          <Badge tone="neutral">Stopped</Badge>
        ) : (
          <StatusBadge status={s.lastDeployStatus} />
        ),
    },
    {
      key: 'lastDeployed',
      header: 'Last deployed',
      cell: (s) =>
        s.lastDeployedAt ? (
          <span className="tnum text-[13px] text-text-2" title={absoluteTitle(s.lastDeployedAt)}>
            {timeAgo(s.lastDeployedAt)}
          </span>
        ) : (
          <span className="text-text-3">—</span>
        ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (s) => (
        <div className="flex items-center justify-end gap-1">
          <DeployButton stack={s} />
          <StopStartButton stack={s} />
          <DeleteButton stack={s} />
        </div>
      ),
    },
  ]

  const renderCard = (s: Stack) => (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3">
        <Link
          to="/stacks/$id"
          params={{ id: String(s.id) }}
          className="inline-flex items-center gap-2 font-medium text-text hover:text-brand"
        >
          <StatusDot status={s.lastDeployStatus} />
          {s.name}
        </Link>
        {s.desiredState === 'stopped' ? (
          <Badge tone="neutral">Stopped</Badge>
        ) : (
          <StatusBadge status={s.lastDeployStatus} />
        )}
      </div>

      {/* Mobile parity with the table: the product names the source, and the same cell qualifies it
          with what a Deploy from this card would apply — the branch, or the release version. */}
      <p className="truncate text-[13px] text-text-2" title={s.repositoryUrl}>
        {s.productName} ·{' '}
        <span className="font-mono">{(usesReleases(s) ? deployTargetVersion(s) : s.branch) ?? '—'}</span>
        {/* Guarded like the desktop VersionCell: a pin surviving a Releases→Git revert is inert
            in Git mode and must not decorate a branch label. */}
        {usesReleases(s) && s.pinnedRelease && (
          <>
            {' '}
            <Badge tone="neutral" size="sm">
              pinned
            </Badge>
          </>
        )}
      </p>

      <p className="text-[13px] text-text-2">
        Last deployed{' '}
        {s.lastDeployedAt ? (
          <span className="tnum" title={absoluteTitle(s.lastDeployedAt)}>
            {timeAgo(s.lastDeployedAt)}
          </span>
        ) : (
          <span className="text-text-3">never</span>
        )}
      </p>

      <div className="flex items-center justify-between border-t border-border pt-3">
        <div className="flex items-center gap-1">
          <DeployButton stack={s} />
          <StopStartButton stack={s} />
        </div>
        <DeleteButton stack={s} />
      </div>
    </div>
  )

  const emptyState = (
    <EmptyState
      icon={Boxes}
      title="No stacks yet"
      description="Register a git repo with a compose file to start deploying."
      action={
        // The mirror of the Products empty state's "Just deploying one repo?" line: the two pages
        // teach each other's entry point, so whichever one a fresh operator lands on first names the
        // other workflow — a sentence for the other persona, not a competing button.
        <div className="flex flex-col items-center gap-3">
          <Button asChild variant="primary">
            <Link to="/stacks/new">
              <Plus /> New stack
            </Link>
          </Button>
          {productsEnabled && (
            <p className="text-[13px] text-text-2">
              Running one product for many tenants?{' '}
              <Link to="/products" className="text-brand hover:underline">
                Start from Products
              </Link>
              .
            </p>
          )}
        </div>
      }
    />
  )

  const filteredEmptyState = (
    <EmptyState
      icon={Boxes}
      title="No matching stacks"
      description={
        status === 'ok'
          ? 'No stacks are currently healthy.'
          : 'No stacks have a failed deploy.'
      }
      action={
        <Button variant="secondary" onClick={clearFilter}>
          Clear filter
        </Button>
      }
    />
  )

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Stacks</h1>
          {status && (
            <button
              type="button"
              onClick={clearFilter}
              className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2.5 py-1 text-xs font-medium text-text-2 hover:bg-surface-3"
            >
              {FILTER_LABEL[status]}
              <X className="size-3.5" aria-label="Clear filter" />
            </button>
          )}
          {isFastPolling && (
            <span className="inline-flex items-center gap-1.5 text-xs font-medium text-run">
              <span className="size-1.5 rounded-full bg-run motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]" />
              live
            </span>
          )}
        </div>
        <Button asChild variant="primary">
          <Link to="/stacks/new">
            <Plus /> New stack
          </Link>
        </Button>
      </div>

      {isError && (
        <Banner
          tone="danger"
          title="Couldn’t load stacks"
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
          items={filtered}
          getKey={(s) => s.id}
          columns={columns}
          renderCard={renderCard}
          skeletonRows={isLoading ? 5 : undefined}
          emptyState={stacks.length === 0 ? emptyState : filteredEmptyState}
          aria-label="Stacks"
        />
      )}

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.name}?` : 'Delete stack?'}
        description="This permanently deletes the stack and its deployment history. Running containers are not affected."
        confirmLabel="Delete"
        tone="danger"
        loading={remove.isPending}
        requireText={pendingDelete?.name}
        onConfirm={() => {
          if (pendingDelete) remove.mutate(pendingDelete)
        }}
      />
    </div>
  )
}

function StatusDot({ status }: { status: Stack['lastDeployStatus'] }) {
  const tone =
    status === 'success'
      ? 'bg-ok'
      : status === 'failed'
        ? 'bg-danger'
        : status === 'running'
          ? 'bg-run'
          : status === 'queued'
            ? 'bg-queue'
            : 'bg-neutral'
  const live = status === 'running' || status === 'queued'
  return (
    <span
      aria-hidden
      className={cn(
        'size-2 shrink-0 rounded-full',
        tone,
        live && 'motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]',
      )}
    />
  )
}
