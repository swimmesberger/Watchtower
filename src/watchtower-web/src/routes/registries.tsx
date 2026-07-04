import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, CheckCircle2, Plus, Trash2, Zap, CheckCircle, XCircle, Loader2 } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateRegistryRequest } from '@/lib/types'

export function RegistriesPage() {
  const qc = useQueryClient()
  const { data: registries = [], isLoading } = useQuery({
    queryKey: ['registries'],
    queryFn: api.registries.list,
  })
  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })
  const { data: dockerConfig } = useQuery({
    queryKey: ['docker-config'],
    queryFn: api.system.dockerConfig,
  })

  const [showForm, setShowForm] = useState(false)
  const [testResults, setTestResults] = useState<Record<number, 'ok' | 'fail' | 'loading'>>({})

  const create = useMutation({
    mutationFn: (data: CreateRegistryRequest) => api.registries.create(data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['registries'] })
      setShowForm(false)
    },
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.registries.delete(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['registries'] }),
  })

  async function testRegistry(id: number) {
    setTestResults(prev => ({ ...prev, [id]: 'loading' }))
    try {
      await api.registries.test(id)
      setTestResults(prev => ({ ...prev, [id]: 'ok' }))
    } catch {
      setTestResults(prev => ({ ...prev, [id]: 'fail' }))
    }
  }

  return (
    <div className="p-6 space-y-4 max-w-[1400px] mx-auto">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-[22px] font-bold tracking-tight">Docker Registries</h1>
          <p className="text-[13px] text-[var(--text-tertiary)] mt-0.5">Configure private image registries for your stacks</p>
        </div>
        <button
          onClick={() => setShowForm(s => !s)}
          className="inline-flex items-center gap-1.5 rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px transition-all"
        >
          <Plus className="size-4" /> Add Registry
        </button>
      </div>

      {dockerConfig && <DockerConfigBanner status={dockerConfig} />}

      {showForm && (
        <RegistryForm
          credentials={credentials}
          onSubmit={data => create.mutate(data)}
          saving={create.isPending}
          error={create.error?.message ?? null}
          onCancel={() => setShowForm(false)}
        />
      )}

      {isLoading ? (
        <div className="flex justify-center py-8">
          <Loader2 className="size-5 animate-spin text-[var(--text-secondary)]" />
        </div>
      ) : registries.length === 0 && !showForm ? (
        <p className="text-sm text-[var(--text-secondary)] text-center py-8">
          No registries configured. Add one to authenticate private image pulls.
        </p>
      ) : (
        <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] overflow-hidden">
        <table className="w-full text-sm border-collapse">
          <thead>
            <tr className="text-left">
              <th className="text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] px-4 py-3 bg-[var(--card)]">Name</th>
              <th className="text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] px-4 py-3 bg-[var(--card)]">URL</th>
              <th className="text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] px-4 py-3 bg-[var(--card)]">Credential</th>
              <th className="text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] px-4 py-3 bg-[var(--card)] sr-only">Actions</th>
            </tr>
          </thead>
          <tbody>
            {registries.map(r => (
              <tr key={r.id} className="border-b border-[rgba(255,255,255,0.06)] hover:bg-[var(--accent)] transition-colors">
                <td className="px-4 py-3 font-medium">{r.name}</td>
                <td className="px-4 py-3 font-[var(--font-mono)] text-xs text-[var(--text-tertiary)]">{r.url}</td>
                <td className="px-4 py-3 text-[var(--text-secondary)]">
                  {r.credentialName ?? <span className="italic">None</span>}
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <TestButton
                      state={testResults[r.id]}
                      onClick={() => testRegistry(r.id)}
                    />
                    <button
                      onClick={() => {
                        if (confirm(`Delete registry "${r.name}"?`)) remove.mutate(r.id)
                      }}
                      aria-label={`Delete ${r.name}`}
                      className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--danger-bg)] text-[var(--danger)] transition-colors"
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

function DockerConfigBanner({ status }: { status: { exists: boolean; path: string; source: string } }) {
  const configDir = status.path.replace(/\/config\.json$/, '')

  if (status.exists) {
    const label = status.source === 'default'
      ? '~/.docker/config.json'
      : `${status.source} → ${status.path}`
    return (
      <div className="flex gap-2.5 rounded-[10px] bg-[var(--success-bg)] border border-[var(--success-border)] px-3.5 py-3 text-xs text-[var(--text-secondary)]">
        <CheckCircle2 className="size-4 mt-0.5 shrink-0 text-[var(--success)]" />
        <span>
          Docker credentials found at <code className="font-[var(--font-mono)] text-[11px] bg-[var(--success-bg)] px-1.5 py-0.5 rounded">{label}</code>.
          Private registry image pulls will use these credentials automatically.
        </span>
      </div>
    )
  }

  const mountFlag = status.source === 'WATCHTOWER_DOCKER_CONFIG'
    ? `-v $HOME/.docker:${configDir}:ro`
    : `-v $HOME/.docker:${configDir}:ro\n-e WATCHTOWER_DOCKER_CONFIG=${configDir}`

  return (
    <div className="rounded-[10px] border border-[var(--warning-border)] bg-[var(--warning-bg)] px-3.5 py-3 text-xs text-[var(--text-secondary)] space-y-2">
      <div className="flex items-center gap-1.5 font-medium">
        <AlertTriangle className="size-4 shrink-0 text-[var(--warning)]" />
        No Docker credentials file found
      </div>
      <p>
        Private image pulls will fail unless credentials are available.
        If Watchtower runs inside a container, mount the host Docker config and set the env var:
      </p>
      <pre className="font-[var(--font-mono)] bg-[rgba(245,158,11,0.08)] rounded-md px-2.5 py-1.5 overflow-x-auto whitespace-pre-wrap">{mountFlag}</pre>
      <p>
        You can also use <code className="font-[var(--font-mono)]">DOCKER_CONFIG</code> if that env var is already set on the host.
        {status.source !== 'default' && (
          <> Currently configured at <code className="font-[var(--font-mono)]">{status.path}</code> but the file does not exist.</>
        )}
      </p>
    </div>
  )
}

function TestButton({ state, onClick }: { state: 'ok' | 'fail' | 'loading' | undefined; onClick: () => void }) {
  if (state === 'loading') return <Loader2 className="size-4 animate-spin text-[var(--text-secondary)]" />
  if (state === 'ok') return <CheckCircle className="size-4 text-[var(--success)]" aria-label="Login succeeded" />
  if (state === 'fail') return <XCircle className="size-4 text-[var(--danger)]" aria-label="Login failed" />
  return (
    <button
      onClick={onClick}
      aria-label="Test registry login"
      className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--accent)] transition-colors"
    >
      <Zap className="size-4" />
    </button>
  )
}

function RegistryForm({
  credentials,
  onSubmit,
  saving,
  error,
  onCancel,
}: {
  credentials: { id: number; name: string; username: string }[]
  onSubmit: (data: CreateRegistryRequest) => void
  saving: boolean
  error: string | null
  onCancel: () => void
}) {
  const [form, setForm] = useState<CreateRegistryRequest>({
    name: '',
    url: 'ghcr.io',
    credentialId: null,
  })

  function handle(e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) {
    const { name, value } = e.target
    setForm(prev => ({
      ...prev,
      [name]: name === 'credentialId' ? (value ? Number(value) : null) : value,
    }))
  }

  return (
    <form
      onSubmit={e => { e.preventDefault(); onSubmit(form) }}
      className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 space-y-4"
    >
      <h2 className="text-sm font-semibold">New Registry</h2>
      <div className="grid grid-cols-2 gap-3">
        <div className="space-y-1">
          <label className="text-xs font-semibold text-[var(--text-secondary)]">Name <span className="text-[var(--danger)]">*</span></label>
          <input name="name" value={form.name} onChange={handle} required className="input w-full" placeholder="GitHub Container Registry" />
        </div>
        <div className="space-y-1">
          <label className="text-xs font-semibold text-[var(--text-secondary)]">URL <span className="text-[var(--danger)]">*</span></label>
          <input name="url" value={form.url} onChange={handle} required className="input w-full" placeholder="ghcr.io" />
        </div>
        <div className="col-span-2 space-y-1">
          <label className="text-xs font-semibold text-[var(--text-secondary)]">Credential</label>
          <select name="credentialId" value={form.credentialId ?? ''} onChange={handle} className="input w-full">
            <option value="">None (unauthenticated)</option>
            {credentials.map(c => (
              <option key={c.id} value={c.id}>{c.name} ({c.username})</option>
            ))}
          </select>
        </div>
      </div>
      {error && <p role="alert" className="text-xs text-[var(--danger)]">{error}</p>}
      <div className="flex gap-2">
        <button type="submit" disabled={saving} className="rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px disabled:opacity-50 transition-all">
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button type="button" onClick={onCancel} className="rounded-[10px] border border-[rgba(255,255,255,0.06)] px-4 py-2 text-[13px] font-semibold hover:bg-[var(--accent)] transition-colors">
          Cancel
        </button>
      </div>
    </form>
  )
}
