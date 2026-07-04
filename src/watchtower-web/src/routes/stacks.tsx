import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { CheckCircle, Clock, Loader2, Play, Plus, Trash2, XCircle } from 'lucide-react'
import { api } from '@/lib/api'
import type { Stack } from '@/lib/types'

export function StacksPage() {
  const qc = useQueryClient()

  const { data: stacks = [], isLoading } = useQuery({
    queryKey: ['stacks'],
    queryFn: api.stacks.list,
    // Poll every 2s while any stack is actively deploying or waiting in queue.
    // The backend eagerly sets Running/Queued status before returning the 202, so the
    // first refetch after a deploy mutation will already see the correct status.
    refetchInterval: (q) => {
      const data = q.state.data ?? []
      return data.some(s => s.lastDeployStatus === 'running' || s.lastDeployStatus === 'queued')
        ? 2000
        : false
    },
  })

  const deploy = useMutation({
    mutationFn: (id: number) => api.stacks.deploy(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['stacks'] }),
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.stacks.delete(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['stacks'] }),
  })

  return (
    <div className="p-6 space-y-4 max-w-[1400px] mx-auto">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-[22px] font-bold tracking-tight">Stacks</h1>
          <p className="text-[13px] text-[var(--text-tertiary)]">Manage your deployment stacks</p>
        </div>
        <Link
          to="/stacks/new"
          className="inline-flex items-center gap-1.5 rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px transition-all"
        >
          <Plus className="size-4" /> New Stack
        </Link>
      </div>

      {isLoading ? (
        <div className="flex justify-center py-8">
          <Loader2 className="size-5 animate-spin text-[var(--text-secondary)]" />
        </div>
      ) : stacks.length === 0 ? (
        <p className="text-sm text-[var(--text-secondary)] text-center py-8">No stacks configured.</p>
      ) : (
        <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] overflow-hidden">
        <table className="w-full text-sm border-collapse">
          <thead>
            <tr className="text-left text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] bg-[var(--card)]">
              <th className="px-4 py-3">Name</th>
              <th className="px-4 py-3">Repository</th>
              <th className="px-4 py-3">Branch</th>
              <th className="px-4 py-3">Status</th>
              <th className="px-4 py-3">Last Deployed</th>
              <th className="px-4 py-3 sr-only">Actions</th>
            </tr>
          </thead>
          <tbody>
            {stacks.map(stack => (
              <tr key={stack.id} className="border-b border-[rgba(255,255,255,0.06)] hover:bg-[var(--accent)] animate-[wt-card-in_0.4s_ease-out_both]">
                <td className="px-4 py-3">
                  <Link
                    to="/stacks/$id"
                    params={{ id: String(stack.id) }}
                    className="font-medium hover:text-[var(--accent-bright)] transition-colors"
                  >
                    {stack.name}
                  </Link>
                  {stack.hasUpdates === true && (
                    <span className="ml-2 inline-flex items-center rounded-full text-[var(--warning)] bg-[var(--warning-bg)] border border-[var(--warning-border)] px-2 py-0.5 text-xs font-medium">
                      Updates available
                    </span>
                  )}
                </td>
                <td className="px-4 py-3 text-[var(--text-tertiary)] max-w-48 truncate">
                  {stack.repositoryUrl.replace('https://github.com/', '')}
                </td>
                <td className="px-4 py-3 font-[var(--font-mono)] text-xs text-[var(--text-tertiary)]">{stack.branch}</td>
                <td className="px-4 py-3">
                  <StatusBadge status={stack.lastDeployStatus} />
                </td>
                <td className="px-4 py-3 font-[var(--font-mono)] text-xs text-[var(--text-tertiary)]">
                  {stack.lastDeployedAt
                    ? new Date(stack.lastDeployedAt).toLocaleString()
                    : '—'}
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => deploy.mutate(stack.id)}
                      disabled={deploy.isPending || stack.lastDeployStatus === 'running' || stack.lastDeployStatus === 'queued'}
                      aria-label={`Deploy ${stack.name}`}
                      className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] disabled:opacity-40 transition-colors"
                    >
                      {deploy.isPending && deploy.variables === stack.id
                        ? <Loader2 className="size-4 animate-spin" />
                        : <Play className="size-4" />}
                    </button>
                    <button
                      onClick={() => {
                        if (confirm(`Delete stack "${stack.name}"?`)) remove.mutate(stack.id)
                      }}
                      aria-label={`Delete ${stack.name}`}
                      className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--danger-bg)] text-[var(--danger)] disabled:opacity-40 transition-colors"
                    >
                      <Trash2 className="size-4" />
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        </div>
      )}
    </div>
  )
}

function StatusBadge({ status }: { status: Stack['lastDeployStatus'] }) {
  if (status === 'success')
    return (
      <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold px-2.5 py-0.5 rounded-full text-[var(--success)] bg-[var(--success-bg)]">
        <CheckCircle className="size-3" /> Success
      </span>
    )
  if (status === 'failed')
    return (
      <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold px-2.5 py-0.5 rounded-full text-[var(--danger)] bg-[var(--danger-bg)]">
        <XCircle className="size-3" /> Failed
      </span>
    )
  if (status === 'running')
    return (
      <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold px-2.5 py-0.5 rounded-full text-[var(--running)] bg-[var(--running-bg)]">
        <Loader2 className="size-3 animate-spin" /> Running
      </span>
    )
  if (status === 'queued')
    return (
      <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold px-2.5 py-0.5 rounded-full text-[var(--queued)] bg-[var(--queued-bg)]">
        <Clock className="size-3" /> Queued
      </span>
    )
  return <span className="text-[11px] text-[var(--text-secondary)]">—</span>
}
