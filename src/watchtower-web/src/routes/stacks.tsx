import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link } from '@tanstack/react-router'
import { Boxes, Play, Plus, Trash2, X } from 'lucide-react'
import { api } from '@/lib/api'
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

const repoLabel = (url: string) => url.replace(/^https:\/\/github\.com\//, '')

export function StacksPage() {
  const qc = useQueryClient()
  const { status } = stacksApi.useSearch()
  const navigate = stacksApi.useNavigate()

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
        disabled={isDeploying(stack)}
        onClick={() => deploy.mutate(stack)}
      >
        <Play /> Deploy
      </Button>
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
      key: 'repo',
      header: 'Repository',
      cell: (s) => (
        <span className="block max-w-[22ch] truncate font-mono text-[13px] text-text-2">
          {repoLabel(s.repositoryUrl)}
        </span>
      ),
    },
    {
      key: 'branch',
      header: 'Branch',
      cell: (s) => <Badge tone="neutral">{s.branch}</Badge>,
    },
    {
      key: 'status',
      header: 'Status',
      cell: (s) => <StatusBadge status={s.lastDeployStatus} />,
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
        <StatusBadge status={s.lastDeployStatus} />
      </div>

      <p className="truncate font-mono text-[13px] text-text-2">
        {repoLabel(s.repositoryUrl)} · {s.branch}
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
        <DeployButton stack={s} />
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
        <Button asChild variant="primary">
          <Link to="/stacks/new">
            <Plus /> New stack
          </Link>
        </Button>
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
    <div className="mx-auto max-w-[1200px] space-y-6 p-4 md:p-6">
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
