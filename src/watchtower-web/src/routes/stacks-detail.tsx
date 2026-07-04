import { useState, useEffect, useRef } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useParams, useNavigate } from '@tanstack/react-router'
import { ArrowLeft, Check, CheckCircle2, ChevronDown, ChevronRight, Copy, Eye, EyeOff, Loader2, Play, Plus, RefreshCw, RotateCcw, Square, Trash2 } from 'lucide-react'
import { api } from '@/lib/api'
import { apiBase } from '@/lib/config'
import type { Credential, DeployEvent, Stack, StackEnvVarInput, UpdateStackRequest } from '@/lib/types'
import { ContainerLogs } from '@/components/container-logs'

type Tab = 'overview' | 'settings'

export function StackDetailPage() {
  const { id } = useParams({ from: '/stacks/$id' })
  const stackId = Number(id)
  const qc = useQueryClient()
  const navigate = useNavigate()
  const [tab, setTab] = useState<Tab>('overview')

  const { data: stack, isLoading: stackLoading } = useQuery({
    queryKey: ['stacks', stackId],
    queryFn: () => api.stacks.get(stackId),
    refetchInterval: (q) => {
      const s = q.state.data?.lastDeployStatus
      return (s === 'running' || s === 'queued') ? 3000 : false
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
    },
  })

  const restart = useMutation({
    mutationFn: (containerId: string) => api.containers.restart(containerId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['containers'] }),
  })

  const stop = useMutation({
    mutationFn: (containerId: string) => api.containers.stop(containerId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['containers'] }),
  })

  const remove = useMutation({
    mutationFn: (containerId: string) => api.containers.remove(containerId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['containers'] }),
  })

  const deleteStack = useMutation({
    mutationFn: () => api.stacks.delete(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      navigate({ to: '/stacks' })
    },
  })

  if (stackLoading) {
    return (
      <div className="flex items-center justify-center h-full text-[var(--text-secondary)]">
        <Loader2 className="size-5 animate-spin mr-2" /> Loading…
      </div>
    )
  }

  if (!stack) {
    return (
      <div className="p-6">
        <p className="text-[var(--danger)]">Stack not found.</p>
      </div>
    )
  }

  const stackContainers = containers.filter(c => c.stackName === stack.composeProjectName)

  return (
    <div className="p-6 space-y-6 max-w-[1400px] mx-auto">
      {/* Back link */}
      <button
        onClick={() => navigate({ to: '/stacks' })}
        className="inline-flex items-center gap-1.5 text-xs text-[var(--text-tertiary)] hover:text-[var(--text-primary)] transition-colors"
        aria-label="Back to stacks"
      >
        <ArrowLeft className="size-3.5" />
        Back to stacks
      </button>

      {/* Header */}
      <div className="flex items-center justify-between gap-4">
        <div className="flex-1 min-w-0">
          <h1 className="text-[22px] font-bold tracking-tight">{stack.name}</h1>
          <p className="text-[13px] text-[var(--text-tertiary)] font-[var(--font-mono)] mt-0.5 truncate">
            {stack.repositoryUrl} · {stack.branch} · {stack.composeFilePath}
          </p>
        </div>
        <button
          onClick={() => deploy.mutate()}
          disabled={deploy.isPending || isDeploying}
          className="rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px transition-all disabled:opacity-50 inline-flex items-center gap-1.5"
        >
          {(deploy.isPending || isDeploying)
            ? <Loader2 className="size-4 animate-spin" />
            : <Play className="size-4" />}
          Deploy
        </button>
      </div>

      {/* Status bar */}
      {isDeploying && (
        <div className="rounded-[10px] bg-[var(--running-bg)] border border-[var(--running-border)] px-3.5 py-2.5 text-xs text-[var(--running)] inline-flex items-center gap-2">
          <Loader2 className="size-3.5 animate-spin" />
          Deployment in progress…
        </div>
      )}
      {!isDeploying && stack.lastDeployStatus === 'success' && (
        <div className="rounded-[10px] bg-[var(--success-bg)] border border-[var(--success-border)] px-3.5 py-2.5 text-xs text-[var(--success)] inline-flex items-center gap-2">
          <CheckCircle2 className="size-3.5" />
          Last deploy successful
        </div>
      )}
      {!isDeploying && stack.lastDeployStatus === 'failed' && (
        <div className="rounded-[10px] bg-[var(--danger-bg)] border border-[var(--danger-border)] px-3.5 py-2.5 text-xs text-[var(--danger)] inline-flex items-center gap-2">
          Last deploy failed
        </div>
      )}

      {/* Tab navigation */}
      <div className="flex gap-0 border-b border-[rgba(255,255,255,0.06)]">
        {(['overview', 'settings'] as const).map(t => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2.5 text-[13px] capitalize -mb-px border-b-2 transition-colors ${
              tab === t
                ? 'font-semibold text-[var(--primary)] border-[var(--primary)]'
                : 'font-medium text-[var(--text-tertiary)] border-transparent hover:text-[var(--text-primary)]'
            }`}
          >
            {t}
          </button>
        ))}
      </div>

      {/* Overview tab */}
      {tab === 'overview' && (
        <div className="space-y-6">
          {/* Containers */}
          <section>
            <h2 className="text-sm font-semibold mb-3">
              Containers
              <span className="ml-1.5 font-normal text-[var(--text-tertiary)]">
                ({stackContainers.length})
              </span>
            </h2>
            {stackContainers.length === 0 ? (
              <p className="text-sm text-[var(--text-secondary)]">
                No running containers for project "{stack.composeProjectName}".
              </p>
            ) : (
              <div className="space-y-3">
                {stackContainers.map(container => {
                  const name = container.names[0]?.replace(/^\//, '') ?? container.id.slice(0, 12)
                  const statusColor =
                    container.health === 'unhealthy'
                      ? { text: 'text-[var(--danger)]', bg: 'bg-[var(--danger-bg)]', border: 'border-[var(--danger-border)]' }
                      : container.health === 'starting'
                        ? { text: 'text-[var(--warning)]', bg: 'bg-[var(--warning-bg)]', border: 'border-[var(--warning-border)]' }
                        : container.state === 'running'
                          ? { text: 'text-[var(--running)]', bg: 'bg-[var(--running-bg)]', border: 'border-[var(--running-border)]' }
                          : { text: 'text-[var(--text-tertiary)]', bg: 'bg-[var(--secondary)]', border: 'border-[var(--border)]' }
                  return (
                    <div key={container.id} className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] overflow-hidden animate-[wt-card-in_0.4s_ease-out_both]">
                      <div className="px-4 py-3 flex items-center justify-between border-b border-[rgba(255,255,255,0.06)]">
                        <div className="flex items-center gap-3 min-w-0">
                          <span className="font-semibold text-sm truncate">{name}</span>
                          <span className={`inline-flex items-center gap-1.5 text-[11px] font-semibold px-2.5 py-0.5 rounded-full ${statusColor.text} ${statusColor.bg} border ${statusColor.border}`}>
                            {container.health ?? container.state}
                          </span>
                        </div>
                        <div className="flex items-center gap-1">
                          <button
                            onClick={() => restart.mutate(container.id)}
                            disabled={restart.isPending}
                            aria-label={`Restart ${name}`}
                            className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] transition-colors text-[var(--text-tertiary)] hover:text-[var(--text-primary)] disabled:opacity-40"
                          >
                            <RotateCcw className="size-3.5" />
                          </button>
                          <button
                            onClick={() => stop.mutate(container.id)}
                            disabled={stop.isPending}
                            aria-label={`Stop ${name}`}
                            className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] transition-colors text-[var(--text-tertiary)] hover:text-[var(--text-primary)] disabled:opacity-40"
                          >
                            <Square className="size-3.5" />
                          </button>
                          <button
                            onClick={() => {
                              if (confirm(`Remove container "${name}"?`)) remove.mutate(container.id)
                            }}
                            aria-label={`Remove ${name}`}
                            className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--danger-bg)] hover:text-[var(--danger)] transition-colors text-[var(--text-tertiary)] disabled:opacity-40"
                          >
                            <Trash2 className="size-3.5" />
                          </button>
                        </div>
                      </div>
                      <div className="grid grid-cols-2 gap-x-6 gap-y-2 px-4 py-3 text-xs">
                        <div>
                          <span className="text-[var(--text-tertiary)]">Image</span>
                          <p className="text-[var(--text-secondary)] font-[var(--font-mono)] text-[11px] truncate">{container.image}</p>
                        </div>
                        <div>
                          <span className="text-[var(--text-tertiary)]">Status</span>
                          <p className="text-[var(--text-secondary)] font-[var(--font-mono)] text-[11px]">{container.status}</p>
                        </div>
                      </div>
                      <ContainerLogs containerId={container.id} containerName={name} />
                    </div>
                  )
                })}
              </div>
            )}
          </section>

          {/* Image Updates */}
          <section>
            <h2 className="text-sm font-semibold mb-3">Image Updates</h2>
            <ImageUpdatesPanel stack={stack} onChecked={updated => qc.setQueryData(['stacks', stackId], updated)} />
          </section>

          {/* Webhook */}
          {stack.webhookEnabled && (
            <section>
              <h2 className="text-sm font-semibold mb-3">Webhook</h2>
              <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] overflow-hidden">
                <WebhookPanel stackId={stackId} token={stack.webhookToken} />
              </div>
            </section>
          )}

          {/* Deploy history */}
          <section>
            <h2 className="text-sm font-semibold mb-3">
              Deploy History
              <span className="ml-1.5 font-normal text-[var(--text-tertiary)]">
                ({events.length})
              </span>
            </h2>
            {events.length === 0 ? (
              <p className="text-sm text-[var(--text-secondary)]">No deployments yet.</p>
            ) : (
              <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] overflow-hidden">
                {events.map(event => (
                  <DeployEventRow key={event.id} event={event} />
                ))}
              </div>
            )}
          </section>
        </div>
      )}

      {/* Settings tab */}
      {tab === 'settings' && (
        <SettingsTab
          stackId={stackId}
          stack={stack}
          envVars={envVars}
          credentials={credentials}
          deleteStack={deleteStack}
        />
      )}
    </div>
  )
}

function ImageUpdatesPanel({
  stack,
  onChecked,
}: {
  stack: Stack
  onChecked: (updated: Stack) => void
}) {
  const checkMutation = useMutation({
    mutationFn: () => api.stacks.checkUpdates(stack.id),
    onSuccess: onChecked,
  })

  const checkedAt = stack.updatesCheckedAt
    ? new Date(stack.updatesCheckedAt).toLocaleString()
    : null

  return (
    <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] overflow-hidden">
      <div className="px-4 py-3 flex items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          {stack.hasUpdates == null && (
            <span className="text-[var(--text-secondary)] text-xs">Never checked</span>
          )}
          {stack.hasUpdates === false && (
            <>
              <CheckCircle2 className="size-4 text-[var(--success)] shrink-0" />
              <span className="text-[var(--text-secondary)] text-xs">
                All images up to date
                {checkedAt && <span className="ml-1 text-[11px] text-[var(--text-tertiary)]">· {checkedAt}</span>}
              </span>
            </>
          )}
          {stack.hasUpdates === true && (
            <div className="space-y-1.5">
              <p className="font-medium text-[var(--warning)] text-xs">
                Updates available
                {checkedAt && <span className="ml-1 text-[11px] font-normal text-[var(--text-tertiary)]">· {checkedAt}</span>}
              </p>
              <ul className="space-y-0.5">
                {(stack.outdatedImages ?? []).map(img => (
                  <li key={img} className="font-[var(--font-mono)] text-[11px] text-[var(--text-secondary)]">· {img}</li>
                ))}
              </ul>
            </div>
          )}
        </div>
        <button
          onClick={() => checkMutation.mutate()}
          disabled={checkMutation.isPending}
          className="rounded-[10px] border border-[var(--border)] px-3 py-1.5 text-xs font-medium text-[var(--text-secondary)] hover:bg-[var(--accent)] hover:text-[var(--text-primary)] transition-all disabled:opacity-50 flex items-center gap-1.5 shrink-0"
        >
          {checkMutation.isPending
            ? <Loader2 className="size-3.5 animate-spin" />
            : <RefreshCw className="size-3.5" />}
          Check now
        </button>
      </div>
    </div>
  )
}

function WebhookPanel({ stackId, token }: { stackId: number; token: string | null }) {
  const [copiedUrl, setCopiedUrl] = useState(false)
  const [copiedToken, setCopiedToken] = useState(false)
  const [showToken, setShowToken] = useState(false)
  const url = `${(apiBase || window.location.origin)}/api/webhooks/stacks/${stackId}/deploy`

  function copyUrl() {
    navigator.clipboard.writeText(url)
    setCopiedUrl(true)
    setTimeout(() => setCopiedUrl(false), 2000)
  }

  function copyToken() {
    if (!token) return
    navigator.clipboard.writeText(token)
    setCopiedToken(true)
    setTimeout(() => setCopiedToken(false), 2000)
  }

  return (
    <div className="p-4 space-y-3 text-sm">
      {!token && (
        <p className="text-xs text-[var(--warning)] bg-[var(--warning-bg)] border border-[var(--warning-border)] rounded-[8px] px-3 py-2">
          ⚠️ No token set — this webhook is public and unauthenticated.
        </p>
      )}
      <div>
        <p className="text-xs font-semibold text-[var(--text-secondary)] mb-1">URL</p>
        <div className="flex items-center gap-2">
          <code className="flex-1 truncate font-[var(--font-mono)] text-[11px] text-[var(--text-secondary)] bg-[rgba(255,255,255,0.03)] px-3 py-2 rounded-[8px]">{url}</code>
          <button
            onClick={copyUrl}
            aria-label="Copy webhook URL"
            className="shrink-0 size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] transition-colors text-[var(--text-tertiary)] hover:text-[var(--text-primary)]"
          >
            {copiedUrl ? <Check className="size-3.5 text-[var(--success)]" /> : <Copy className="size-3.5" />}
          </button>
        </div>
      </div>
      {token && (
        <div>
          <p className="text-xs font-semibold text-[var(--text-secondary)] mb-1">Token</p>
          <div className="flex items-center gap-2">
            <code className="flex-1 truncate font-[var(--font-mono)] text-[11px] text-[var(--text-secondary)] bg-[rgba(255,255,255,0.03)] px-3 py-2 rounded-[8px]">
              {showToken ? token : '••••••••••••••••'}
            </code>
            <button
              onClick={() => setShowToken(s => !s)}
              aria-label={showToken ? 'Hide token' : 'Show token'}
              className="shrink-0 size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] transition-colors text-[var(--text-tertiary)] hover:text-[var(--text-primary)]"
            >
              {showToken ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
            </button>
            <button
              onClick={copyToken}
              aria-label="Copy token"
              className="shrink-0 size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] transition-colors text-[var(--text-tertiary)] hover:text-[var(--text-primary)]"
            >
              {copiedToken ? <Check className="size-3.5 text-[var(--success)]" /> : <Copy className="size-3.5" />}
            </button>
          </div>
        </div>
      )}
      <div>
        <p className="text-xs font-semibold text-[var(--text-secondary)] mb-1">Example</p>
        <pre className="font-[var(--font-mono)] text-[11px] text-[var(--text-tertiary)] bg-[rgba(0,0,0,0.3)] rounded-[8px] px-3.5 py-3 overflow-x-auto whitespace-pre">{
          token
            ? `curl -X POST -H "Authorization: Bearer <token>" \\\n  ${url}`
            : `curl -X POST ${url}`
        }</pre>
      </div>
    </div>
  )
}

function SettingsTab({
  stackId,
  stack,
  envVars,
  credentials,
  deleteStack,
}: {
  stackId: number
  stack: { name: string; repositoryUrl: string; composeFilePath: string; branch: string; composeProjectName: string; credentialId: number | null; webhookToken: string | null; webhookEnabled: boolean }
  envVars: { key: string; value: string }[]
  credentials: Credential[]
  deleteStack: { mutate: () => void; isPending: boolean }
}) {
  const qc = useQueryClient()

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

  const [copied, setCopied] = useState(false)

  const [envDraft, setEnvDraft] = useState<StackEnvVarInput[]>([
    ...envVars.map(v => ({ key: v.key, value: v.value })),
    { key: '', value: '' },
  ])
  const [envVisible, setEnvVisible] = useState<Set<number>>(new Set())
  const [saveError, setSaveError] = useState<string | null>(null)

  const update = useMutation({
    mutationFn: (data: UpdateStackRequest) => api.stacks.update(stackId, data),
    onSuccess: (updated) => {
      qc.setQueryData(['stacks', stackId], updated)
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'env'] })
      setSaveError(null)
    },
    onError: (err: Error) => setSaveError(err.message),
  })

  function handle(e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) {
    const { name, value } = e.target
    if (e.target instanceof HTMLInputElement && e.target.type === 'checkbox') {
      setForm(prev => ({ ...prev, [name]: e.target instanceof HTMLInputElement && e.target.checked }))
      return
    }
    setForm(prev => ({
      ...prev,
      [name]: name === 'credentialId' ? (value ? Number(value) : null) : value,
    }))
  }

  function copyWebhookUrl() {
    const url = `${(apiBase || window.location.origin)}/api/webhooks/stacks/${stackId}/deploy`
    navigator.clipboard.writeText(url).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    })
  }

  function updateEnvRow(i: number, field: 'key' | 'value', val: string) {
    setEnvDraft(prev => {
      const next = prev.map((r, idx) => idx === i ? { ...r, [field]: val } : r)
      const last = next.at(-1)
      if (!last || last.key !== '' || last.value !== '') next.push({ key: '', value: '' })
      return next
    })
  }

  function removeEnvRow(i: number) {
    setEnvDraft(prev => {
      const next = prev.filter((_, idx) => idx !== i)
      const tail = next.at(-1)
      if (!tail || tail.key !== '' || tail.value !== '') next.push({ key: '', value: '' })
      return next
    })
  }

  function toggleEnvVisible(i: number) {
    setEnvVisible(prev => { const s = new Set(prev); s.has(i) ? s.delete(i) : s.add(i); return s })
  }

  function handleSave(e: React.FormEvent) {
    e.preventDefault()
    setSaveError(null)
    const validEnv = envDraft.filter(v => v.key.trim() !== '')
    update.mutate({
      ...form,
      composeProjectName: form.composeProjectName || null,
      // Keep empty string as null (no token); the enabled flag controls whether webhook is active.
      webhookToken: form.webhookToken || null,
      envVars: validEnv,
    })
  }

  return (
    <form onSubmit={handleSave} className="space-y-6 max-w-lg">
      {/* Stack configuration */}
      <section className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 space-y-4">
        <h2 className="text-sm font-semibold">Configuration</h2>

        <Field label="Stack Name" required>
          <input name="name" value={form.name} onChange={handle} required className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-primary)] font-[var(--font-mono)] focus:outline-none focus:ring-2 focus:ring-[var(--primary)] focus:border-transparent w-full" />
        </Field>

        <Field label="Repository URL" required>
          <input
            name="repositoryUrl"
            value={form.repositoryUrl}
            onChange={handle}
            required
            placeholder="https://github.com/owner/repo"
            className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-primary)] font-[var(--font-mono)] focus:outline-none focus:ring-2 focus:ring-[var(--primary)] focus:border-transparent w-full"
          />
        </Field>

        <Field label="Branch">
          <input name="branch" value={form.branch} onChange={handle} placeholder="main" className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-primary)] font-[var(--font-mono)] focus:outline-none focus:ring-2 focus:ring-[var(--primary)] focus:border-transparent w-full" />
        </Field>

        <Field label="Compose File Path">
          <input
            name="composeFilePath"
            value={form.composeFilePath}
            onChange={handle}
            placeholder="docker-compose.yml"
            className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-primary)] font-[var(--font-mono)] focus:outline-none focus:ring-2 focus:ring-[var(--primary)] focus:border-transparent w-full"
          />
        </Field>

        <Field label="Compose Project Name" hint="Defaults to stack name if empty">
          <input
            name="composeProjectName"
            value={form.composeProjectName ?? ''}
            onChange={handle}
            className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-primary)] font-[var(--font-mono)] focus:outline-none focus:ring-2 focus:ring-[var(--primary)] focus:border-transparent w-full"
          />
        </Field>

        <Field label="Credential">
          <select name="credentialId" value={form.credentialId ?? ''} onChange={handle} className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-primary)] focus:outline-none focus:ring-2 focus:ring-[var(--primary)] focus:border-transparent w-full">
            <option value="">None (public repository)</option>
            {credentials.map(c => (
              <option key={c.id} value={c.id}>{c.name} ({c.username})</option>
            ))}
          </select>
        </Field>

        <Field label="Webhook" hint={form.webhookEnabled && !form.webhookToken ? '⚠️ No token set — this webhook is public and unauthenticated.' : 'Trigger a deployment from external systems (CI/CD, GitHub Actions, etc.).'}>
          <label className="flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              name="webhookEnabled"
              checked={form.webhookEnabled ?? false}
              onChange={handle}
              className="size-4 rounded border-[var(--border)] accent-[var(--primary)]"
            />
            <span className="text-[13px] text-[var(--text-primary)]">Enable webhook endpoint</span>
          </label>
        </Field>

        {form.webhookEnabled && (
          <>
            <Field label="Webhook Token" hint="Optional bearer token to protect the webhook. Leave blank to allow unauthenticated access.">
              <input
                name="webhookToken"
                value={form.webhookToken ?? ''}
                onChange={handle}
                type="password"
                placeholder="Leave blank for unauthenticated access"
                className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-primary)] font-[var(--font-mono)] focus:outline-none focus:ring-2 focus:ring-[var(--primary)] focus:border-transparent w-full"
              />
            </Field>

            <Field label="Webhook URL">
              <div className="flex gap-2 items-center">
                <input
                  readOnly
                  value={`${typeof window !== 'undefined' ? (apiBase || window.location.origin) : ''}/api/webhooks/stacks/${stackId}/deploy`}
                  className="bg-[var(--secondary)] border border-[var(--border)] rounded-[10px] px-3 py-2.5 text-[13px] text-[var(--text-secondary)] font-[var(--font-mono)] focus:outline-none w-full select-all"
                  aria-label="Webhook URL"
                  onFocus={e => e.currentTarget.select()}
                />
                <button
                  type="button"
                  onClick={copyWebhookUrl}
                  aria-label="Copy webhook URL"
                  className="shrink-0 size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] transition-colors text-[var(--text-tertiary)] hover:text-[var(--text-primary)]"
                  title="Copy URL"
                >
                  {copied ? <Check className="size-4 text-[var(--success)]" /> : <Copy className="size-4" />}
                </button>
              </div>
              <p className="text-[11px] text-[var(--text-tertiary)] mt-1 font-[var(--font-mono)]">
                {form.webhookToken
                  ? `curl -X POST -H "Authorization: Bearer <token>" \\`
                  : 'curl -X POST \\'}
                <br />
                {`  ${typeof window !== 'undefined' ? (apiBase || window.location.origin) : ''}/api/webhooks/stacks/${stackId}/deploy`}
              </p>
            </Field>
          </>
        )}
      </section>

      {/* Environment variables */}
      <section className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 space-y-4">
        <h2 className="text-sm font-semibold">Environment Variables</h2>
        <div className="rounded-[10px] border border-[rgba(255,255,255,0.06)] overflow-hidden">
          <div className="grid grid-cols-[1fr_1fr_2.5rem] bg-[var(--secondary)] px-3 py-1.5 text-xs font-semibold text-[var(--text-secondary)] border-b border-[rgba(255,255,255,0.06)]">
            <span>Key</span>
            <span>Value</span>
            <span />
          </div>
          <div className="divide-y divide-[rgba(255,255,255,0.06)]">
            {envDraft.map((row, i) => {
              const isBlankTrailer = i === envDraft.length - 1
              return (
                <div key={i} className="grid grid-cols-[1fr_1fr_2.5rem] items-center">
                  <input
                    value={row.key}
                    onChange={e => updateEnvRow(i, 'key', e.target.value)}
                    placeholder={isBlankTrailer ? 'NEW_KEY' : ''}
                    spellCheck={false}
                    className="font-[var(--font-mono)] text-[11px] text-[var(--text-primary)] px-3 py-2 bg-transparent border-r border-[rgba(255,255,255,0.06)] focus:outline-none focus:bg-[var(--accent-muted)] placeholder:text-[var(--text-tertiary)] w-full"
                    aria-label={`Key for variable ${i + 1}`}
                  />
                  <div className="flex items-center border-r border-[rgba(255,255,255,0.06)] relative">
                    <input
                      value={row.value}
                      onChange={e => updateEnvRow(i, 'value', e.target.value)}
                      placeholder={isBlankTrailer ? 'value' : ''}
                      spellCheck={false}
                      type={envVisible.has(i) ? 'text' : 'password'}
                      className="font-[var(--font-mono)] text-[11px] text-[var(--text-primary)] px-3 py-2 bg-transparent focus:outline-none focus:bg-[var(--accent-muted)] placeholder:text-[var(--text-tertiary)] w-full pr-8"
                      aria-label={`Value for variable ${i + 1}`}
                    />
                    {(row.value !== '' || !isBlankTrailer) && (
                      <button
                        type="button"
                        onClick={() => toggleEnvVisible(i)}
                        aria-label={envVisible.has(i) ? 'Hide value' : 'Show value'}
                        className="absolute right-2 text-[var(--text-tertiary)] hover:text-[var(--text-primary)] transition-colors"
                      >
                        {envVisible.has(i) ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
                      </button>
                    )}
                  </div>
                  <div className="flex items-center justify-center">
                    {!isBlankTrailer ? (
                      <button
                        type="button"
                        onClick={() => removeEnvRow(i)}
                        aria-label={`Remove ${row.key}`}
                        className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--danger-bg)] hover:text-[var(--danger)] transition-colors text-[var(--text-tertiary)]"
                      >
                        <Trash2 className="size-3.5" />
                      </button>
                    ) : (
                      <Plus className="size-3.5 text-[var(--text-tertiary)]" aria-hidden />
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        </div>
        <p className="text-xs text-[var(--text-tertiary)]">
          Injected via <code className="font-[var(--font-mono)]">--env-file</code> on every deploy.
          Use <code className="font-[var(--font-mono)]">{'${KEY}'}</code> in your compose file to reference them.
        </p>
      </section>

      {/* Save */}
      <div className="flex items-center gap-3">
        <button
          type="submit"
          disabled={update.isPending}
          className="rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px transition-all disabled:opacity-50 inline-flex items-center gap-1.5"
        >
          {update.isPending && <Loader2 className="size-4 animate-spin" />}
          Save Settings
        </button>
        {update.isSuccess && !update.isPending && (
          <span className="text-xs text-[var(--success)]">Saved</span>
        )}
      </div>
      {saveError && (
        <p role="alert" className="text-sm text-[var(--danger)]">{saveError}</p>
      )}

      {/* Danger zone */}
      <section className="rounded-[14px] border border-[var(--danger-border)] bg-[var(--danger-bg)] p-5 space-y-3">
        <h2 className="text-sm font-semibold text-[var(--danger)]">Danger Zone</h2>
        <p className="text-xs text-[var(--text-secondary)]">
          Permanently deletes this stack and all its deployment history. Running containers are not affected.
        </p>
        <button
          type="button"
          onClick={() => {
            if (confirm('Delete this stack? This cannot be undone.')) deleteStack.mutate()
          }}
          disabled={deleteStack.isPending}
          className="rounded-[10px] bg-[var(--danger)] text-white px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_12px_rgba(244,63,94,0.3)] transition-all disabled:opacity-50"
        >
          {deleteStack.isPending ? 'Deleting…' : 'Delete Stack'}
        </button>
      </section>
    </form>
  )
}

function Field({
  label,
  hint,
  required,
  children,
}: {
  label: string
  hint?: string
  required?: boolean
  children: React.ReactNode
}) {
  return (
    <div className="space-y-1">
      <label className="block text-xs font-semibold text-[var(--text-secondary)]">
        {label}
        {required && <span className="text-[var(--danger)] ml-0.5" aria-hidden>*</span>}
      </label>
      {children}
      {hint && <p className="text-xs text-[var(--text-tertiary)]">{hint}</p>}
    </div>
  )
}

function DeployEventRow({ event }: { event: DeployEvent }) {
  const [expanded, setExpanded] = useState(false)
  const [liveLines, setLiveLines] = useState<string[]>([])
  const [streaming, setStreaming] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)
  const esRef = useRef<EventSource | null>(null)

  const isActive = event.status === 'running' || event.status === 'queued'

  // Connect to SSE when the row is expanded and the deploy is (or was) active.
  // For completed events the SSE endpoint replays stored output then sends event:done.
  useEffect(() => {
    if (!expanded) {
      esRef.current?.close()
      esRef.current = null
      setLiveLines([])
      setStreaming(false)
      return
    }

    // Only use SSE when there is something to stream (active) or we want a clean replay.
    // For finished events with no output stored, fall through to the static display.
    setLiveLines([])
    setStreaming(true)

    const es = new EventSource(`${apiBase}/api/stacks/events/${event.id}/stream`)
    esRef.current = es

    es.onmessage = e => {
      setLiveLines(prev => [...prev, e.data])
    }
    es.addEventListener('done', () => {
      setStreaming(false)
      es.close()
    })
    es.onerror = () => {
      setStreaming(false)
      es.close()
    }

    return () => {
      es.close()
    }
  }, [expanded, event.id])

  // Auto-scroll to the bottom as new lines arrive.
  useEffect(() => {
    if (expanded && streaming) bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [liveLines, expanded, streaming])

  const statusIcon =
    event.status === 'success'
      ? <CheckCircle2 className="size-3.5 text-[var(--success)]" />
      : event.status === 'failed'
        ? <span className="size-3.5 text-[var(--danger)]">✕</span>
        : <Loader2 className="size-3.5 animate-spin text-[var(--running)]" />

  return (
    <div className="overflow-hidden">
      <button
        onClick={() => setExpanded(e => !e)}
        className="flex w-full items-center gap-3 px-4 py-3 border-b border-[rgba(255,255,255,0.06)] text-sm hover:bg-[var(--accent)] transition-colors text-left cursor-pointer"
      >
        {expanded
          ? <ChevronDown className="size-4 shrink-0 text-[var(--text-tertiary)]" />
          : <ChevronRight className="size-4 shrink-0 text-[var(--text-tertiary)]" />}
        {statusIcon}
        <span className={`text-[11px] font-semibold ${
          event.status === 'success' ? 'text-[var(--success)]'
            : event.status === 'failed' ? 'text-[var(--danger)]'
              : 'text-[var(--running)]'
        }`}>
          {event.status}
        </span>
        <span className="font-[var(--font-mono)] text-[11px] text-[var(--text-tertiary)]">
          {new Date(event.startedAt).toLocaleString()}
        </span>
        <span className="text-xs text-[var(--text-tertiary)]">via {event.triggeredBy}</span>
        {event.finishedAt && (
          <span className="ml-auto font-[var(--font-mono)] text-[11px] text-[var(--text-tertiary)]">
            {Math.round(
              (new Date(event.finishedAt).getTime() - new Date(event.startedAt).getTime()) / 1000,
            )}s
          </span>
        )}
        {isActive && !event.finishedAt && (
          <span className="ml-auto text-[11px] text-[var(--running)] animate-pulse font-semibold">live</span>
        )}
      </button>
      {expanded && (
        <div className="bg-[rgba(0,0,0,0.3)] rounded-[8px] px-3.5 py-3 mx-4 my-3 overflow-hidden">
          {streaming && isActive && (
            <div className="flex items-center gap-1.5 mb-2 text-[10px] font-semibold text-[var(--running)]">
              <span className="size-1.5 rounded-full bg-[var(--running)] inline-block animate-[wt-pulse-dot_2s_ease-in-out_infinite]" aria-hidden />
              streaming
            </div>
          )}
          <pre className="font-[var(--font-mono)] text-[11px] text-[var(--text-tertiary)] overflow-x-auto whitespace-pre-wrap max-h-[300px] overflow-y-auto">
            {liveLines.length > 0
              ? liveLines.map((line, i) => <span key={i}>{line || '\u00A0'}{'\n'}</span>)
              : isActive
                ? '⏳ Waiting for output…'
                : '(no output)'}
            <div ref={bottomRef} />
          </pre>
        </div>
      )}
    </div>
  )
}

