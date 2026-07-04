import React from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { AlertTriangle, Play, XCircle, Loader2, Plus, Clock, Box } from 'lucide-react'
import { api } from '@/lib/api'
import type { ActiveDeployment, Stack } from '@/lib/types'

export function DashboardPage() {
  const qc = useQueryClient()
  const { data: stacks = [], isLoading } = useQuery({
    queryKey: ['stacks'],
    queryFn: api.stacks.list,
  })
  const { data: containers = [] } = useQuery({
    queryKey: ['containers'],
    queryFn: api.containers.list,
    refetchInterval: 10_000,
  })
  const { data: activeDeployments = [] } = useQuery({
    queryKey: ['deployments', 'active'],
    queryFn: api.deployments.active,
    refetchInterval: 3_000,
  })
  const { data: selfStatus } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: 5 * 60_000,
    retry: false,
  })

  const deploy = useMutation({
    mutationFn: (id: number) => api.stacks.deploy(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['deployments', 'active'] })
    },
  })

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-full text-[var(--text-secondary)]">
        <Loader2 className="size-5 animate-spin mr-2" /> Loading…
      </div>
    )
  }

  return (
    <div className="p-6 space-y-6 max-w-[1400px] mx-auto">
      {/* Watchtower update banner */}
      {selfStatus?.isOutdated && (
        <div className="flex items-center gap-3 rounded-[10px] border border-[var(--warning-border)] bg-[var(--warning-bg)] px-4 py-3 animate-[wt-card-in_0.5s_ease-out]">
          <AlertTriangle className="size-[18px] text-[var(--warning)] shrink-0" />
          <div className="flex-1 min-w-0">
            <p className="text-[13px] font-semibold text-[var(--warning)]">Watchtower update available</p>
            <p className="text-xs text-[var(--text-secondary)] mt-0.5">
              A newer version of <code className="font-[var(--font-mono)] text-[11px]">{selfStatus.imageName}</code> was detected.
            </p>
          </div>
          <Link to="/settings" className="shrink-0 text-xs font-semibold text-[var(--warning)] hover:opacity-80 transition-opacity">
            Go to Settings →
          </Link>
        </div>
      )}

      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-[22px] font-bold tracking-tight">Dashboard</h1>
          <p className="text-[13px] text-[var(--text-tertiary)] mt-0.5">Fleet status at a glance</p>
        </div>
        <Link
          to="/stacks/new"
          className="inline-flex items-center gap-1.5 rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px transition-all"
        >
          <Plus className="size-[15px]" /> New Stack
        </Link>
      </div>

      {/* Summary stats */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <StatCard label="Total Stacks" value={stacks.length} accent="teal" delay={0.05} />
        <StatCard
          label="Healthy"
          value={stacks.filter(s => s.lastDeployStatus === 'success').length}
          accent="green"
          delay={0.10}
        />
        <StatCard
          label="Failed"
          value={stacks.filter(s => s.lastDeployStatus === 'failed').length}
          accent="red"
          delay={0.15}
        />
        <StatCard label="Containers" value={containers.length} accent="blue" delay={0.20} />
      </div>

      {/* Active deployments live feed */}
      {activeDeployments.length > 0 && (
        <ActiveDeploymentsPanel deployments={activeDeployments} />
      )}

      {/* Stack cards */}
      {stacks.length === 0 ? (
        <EmptyState />
      ) : (
        <>
          <div className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-[0.08em] text-[var(--text-tertiary)]">
            All Stacks
            <div className="flex-1 h-px bg-[rgba(255,255,255,0.06)]" />
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-3">
            {stacks.map((stack, i) => (
              <StackCard
                key={stack.id}
                stack={stack}
                containerCount={containers.filter(c => c.stackName === stack.composeProjectName).length}
                onDeploy={() => deploy.mutate(stack.id)}
                deploying={deploy.isPending && deploy.variables === stack.id}
                delay={0.30 + i * 0.05}
              />
            ))}
          </div>
        </>
      )}
    </div>
  )
}

function ActiveDeploymentsPanel({ deployments }: { deployments: ActiveDeployment[] }) {
  return (
    <div className="rounded-[14px] border border-[var(--running-border)] bg-[var(--running-bg)] overflow-hidden animate-[wt-card-in_0.4s_ease-out_0.25s_both]">
      <div className="flex items-center gap-2.5 px-4 py-3 border-b border-[var(--running-border)]">
        <div className="size-3.5 border-2 border-transparent border-t-[var(--running)] rounded-full animate-spin" />
        <span className="text-[13px] font-semibold text-[var(--running)]">Active Deployments</span>
        <span className="text-[11px] font-semibold text-[var(--running)] bg-[rgba(59,130,246,0.15)] px-2 py-0.5 rounded-full">{deployments.length}</span>
      </div>
      <div>
        {deployments.map(d => (
          <ActiveDeploymentRow key={d.id} deployment={d} />
        ))}
      </div>
    </div>
  )
}

function ActiveDeploymentRow({ deployment: d }: { deployment: ActiveDeployment }) {
  const elapsed = useElapsed(d.startedAt)
  const isRunning = d.status === 'running'
  return (
    <div className="flex items-center gap-3 px-4 py-2.5 hover:bg-[rgba(59,130,246,0.06)] transition-colors border-t border-[rgba(59,130,246,0.08)] first:border-t-0">
      <div className={`size-7 flex items-center justify-center rounded-md shrink-0 ${isRunning ? 'bg-[rgba(59,130,246,0.15)]' : 'bg-[var(--queued-bg)]'}`}>
        {isRunning
          ? <div className="size-3 border-[1.5px] border-transparent border-t-[var(--running)] rounded-full animate-spin" />
          : <Clock className="size-3.5 text-[var(--queued)]" />
        }
      </div>
      <div className="flex-1 min-w-0">
        <Link
          to="/stacks/$id"
          params={{ id: String(d.stackId) }}
          className="text-[13px] font-semibold hover:text-[var(--accent-bright)] transition-colors truncate block"
        >
          {d.stackName}
        </Link>
        <p className="text-[11px] text-[var(--text-tertiary)] font-[var(--font-mono)] mt-px">
          triggered {d.triggeredBy} · {elapsed}
        </p>
      </div>
      <span className="text-xs font-[var(--font-mono)] text-[var(--text-tertiary)] tabular-nums w-[4.5em] text-right shrink-0">{elapsed}</span>
      <span className={`shrink-0 text-[11px] font-semibold px-2.5 py-0.5 rounded-full uppercase tracking-wide ${
        isRunning
          ? 'text-[var(--running)] bg-[rgba(59,130,246,0.15)] border border-[rgba(59,130,246,0.2)]'
          : 'text-[var(--queued)] bg-[var(--queued-bg)] border border-[var(--queued-border)]'
      }`}>
        {d.status}
      </span>
    </div>
  )
}

function useElapsed(startedAt: string): string {
  const [now, setNow] = React.useState(() => Date.now())
  React.useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1_000)
    return () => clearInterval(id)
  }, [])
  const seconds = Math.floor((now - new Date(startedAt).getTime()) / 1_000)
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${minutes}m ${secs}s`
}

const accentColors = {
  teal: { bar: 'bg-[var(--primary)]', value: 'text-[var(--accent-bright)]' },
  green: { bar: 'bg-[var(--success)]', value: 'text-[var(--success)]' },
  red: { bar: 'bg-[var(--danger)]', value: 'text-[var(--danger)]' },
  blue: { bar: 'bg-[var(--running)]', value: 'text-[var(--running)]' },
} as const

function StatCard({ label, value, accent, delay }: { label: string; value: number; accent: keyof typeof accentColors; delay: number }) {
  const colors = accentColors[accent]
  return (
    <div
      className="relative rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 overflow-hidden hover:border-[var(--border)] hover:bg-[var(--secondary)] transition-all"
      style={{ animation: `wt-card-in 0.4s ease-out ${delay}s both` }}
    >
      <div className={`absolute top-0 left-0 right-0 h-0.5 rounded-t ${colors.bar}`} />
      <p className="text-[11px] font-medium uppercase tracking-[0.06em] text-[var(--text-tertiary)] mb-2">{label}</p>
      <p className={`text-[32px] font-extrabold tracking-tight leading-none ${colors.value}`}>{value}</p>
    </div>
  )
}

function StackCard({
  stack,
  containerCount,
  onDeploy,
  deploying,
  delay,
}: {
  stack: Stack
  containerCount: number
  onDeploy: () => void
  deploying: boolean
  delay: number
}) {
  const statusClass = stack.lastDeployStatus === 'success'
    ? 'success' : stack.lastDeployStatus === 'failed'
    ? 'failed' : stack.lastDeployStatus === 'running'
    ? 'running' : stack.lastDeployStatus === 'queued'
    ? 'queued' : 'none'

  return (
    <div
      className="relative rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 hover:border-[rgba(255,255,255,0.14)] hover:bg-[var(--secondary)] hover:-translate-y-0.5 hover:shadow-[0_8px_30px_rgba(0,0,0,0.5)] transition-all cursor-pointer overflow-hidden"
      style={{ animation: `wt-card-in 0.4s ease-out ${delay}s both` }}
    >
      {/* Status glow stripe */}
      {statusClass === 'success' && <div className="absolute top-0 left-5 right-5 h-px bg-gradient-to-r from-transparent via-[var(--success)] to-transparent shadow-[0_0_12px_var(--success)]" />}
      {statusClass === 'failed' && <div className="absolute top-0 left-5 right-5 h-px bg-gradient-to-r from-transparent via-[var(--danger)] to-transparent shadow-[0_0_12px_var(--danger)]" />}
      {statusClass === 'running' && <div className="absolute top-0 left-5 right-5 h-px bg-gradient-to-r from-transparent via-[var(--running)] to-transparent shadow-[0_0_12px_var(--running)] animate-[wt-glow-pulse_2s_ease-in-out_infinite]" />}

      <div className="flex items-start justify-between gap-2 mb-3.5">
        <div className="flex gap-2.5 items-start flex-1 min-w-0">
          <StatusIndicator status={stack.lastDeployStatus} />
          <div className="min-w-0">
            <Link
              to="/stacks/$id"
              params={{ id: String(stack.id) }}
              className="text-[15px] font-bold tracking-tight hover:text-[var(--accent-bright)] transition-colors truncate block"
            >
              {stack.name}
            </Link>
            <p className="text-xs font-[var(--font-mono)] text-[var(--text-tertiary)] mt-0.5 truncate">
              {stack.repositoryUrl.replace('https://github.com/', '')}
            </p>
          </div>
        </div>
        <button
          onClick={e => { e.stopPropagation(); onDeploy() }}
          disabled={deploying || stack.lastDeployStatus === 'running'}
          className="inline-flex items-center gap-1 rounded-md bg-[var(--accent-muted)] border border-[rgba(20,184,166,0.2)] text-[var(--primary)] px-3 py-1.5 text-xs font-semibold hover:bg-[rgba(20,184,166,0.2)] hover:border-[rgba(20,184,166,0.35)] hover:shadow-[0_0_12px_rgba(20,184,166,0.15)] transition-all disabled:opacity-40 disabled:cursor-not-allowed"
          aria-label={`Deploy ${stack.name}`}
        >
          {deploying ? <Loader2 className="size-3.5 animate-spin" /> : <Play className="size-3 fill-current" />}
          Deploy
        </button>
      </div>

      {stack.hasUpdates === true && (
        <div className="inline-flex items-center gap-1 text-[10px] font-semibold text-[var(--warning)] bg-[var(--warning-bg)] border border-[var(--warning-border)] px-2 py-0.5 rounded-full mb-3">
          <AlertTriangle className="size-[11px]" />
          {(stack.outdatedImages?.length ?? 0)} image update{(stack.outdatedImages?.length ?? 0) !== 1 ? 's' : ''} available
        </div>
      )}

      <div className="flex items-center gap-3 text-xs text-[var(--text-tertiary)]">
        <span className="flex items-center gap-1">
          <Box className="size-[13px] opacity-60" />
          {containerCount} container{containerCount !== 1 ? 's' : ''}
        </span>
        {stack.lastDeployStatus === 'running' && (
          <span className="text-[var(--running)]">deploying…</span>
        )}
        {stack.lastDeployStatus === 'failed' && stack.lastDeployedAt && (
          <span className="flex items-center gap-1 text-[var(--danger)]">
            <XCircle className="size-[13px]" />
            Failed {timeAgo(stack.lastDeployedAt)}
          </span>
        )}
        {stack.lastDeployStatus === 'success' && stack.lastDeployedAt && (
          <span className="flex items-center gap-1">
            <Clock className="size-[13px] opacity-60" />
            {timeAgo(stack.lastDeployedAt)}
          </span>
        )}
        {!stack.lastDeployStatus && (
          <span className="italic">never deployed</span>
        )}
      </div>
    </div>
  )
}

function StatusIndicator({ status }: { status: Stack['lastDeployStatus'] }) {
  if (status === 'success') return <div className="size-2.5 rounded-full bg-[var(--success)] shadow-[0_0_8px_rgba(16,185,129,0.5)] shrink-0 mt-1.5" />
  if (status === 'failed') return <div className="size-2.5 rounded-full bg-[var(--danger)] shadow-[0_0_8px_rgba(244,63,94,0.5)] shrink-0 mt-1.5" />
  if (status === 'running') return <div className="size-2.5 rounded-full bg-[var(--running)] shadow-[0_0_8px_rgba(59,130,246,0.5)] shrink-0 mt-1.5 animate-[wt-pulse-dot_1.5s_ease-in-out_infinite]" />
  if (status === 'queued') return <div className="size-2.5 rounded-full bg-[var(--queued)] shadow-[0_0_8px_rgba(167,139,250,0.4)] shrink-0 mt-1.5 animate-[wt-pulse-dot_2s_ease-in-out_infinite]" />
  return <div className="size-2.5 rounded-full bg-[var(--text-tertiary)] opacity-40 shrink-0 mt-1.5" />
}

function timeAgo(iso: string): string {
  const seconds = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

function EmptyState() {
  return (
    <div className="flex flex-col items-center justify-center rounded-[20px] border border-dashed border-[var(--border)] py-16 text-center animate-[wt-card-in_0.5s_ease-out_0.3s_both]">
      <div className="size-14 flex items-center justify-center bg-[var(--accent-muted)] rounded-[14px] mb-4">
        <Box className="size-7 text-[var(--primary)]" />
      </div>
      <p className="text-base font-semibold mb-1.5">No stacks yet</p>
      <p className="text-[13px] text-[var(--text-tertiary)] mb-5">Create your first Docker Compose deployment</p>
      <Link
        to="/stacks/new"
        className="inline-flex items-center gap-1.5 rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] transition-all"
      >
        <Plus className="size-4" /> Create Stack
      </Link>
    </div>
  )
}
