import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Info, Plus, Trash2, Loader2 } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateCredentialRequest } from '@/lib/types'

export function CredentialsPage() {
  const qc = useQueryClient()
  const { data: credentials = [], isLoading } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const [showForm, setShowForm] = useState(false)

  const create = useMutation({
    mutationFn: (data: CreateCredentialRequest) => api.credentials.create(data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['credentials'] })
      setShowForm(false)
    },
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.credentials.delete(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['credentials'] }),
  })

  return (
    <div className="p-6 space-y-4 max-w-[1400px] mx-auto">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-[22px] font-bold tracking-tight">Credentials</h1>
          <p className="text-[13px] text-[var(--text-tertiary)] mt-0.5">Manage authentication tokens for repositories and registries</p>
        </div>
        <button
          onClick={() => setShowForm(s => !s)}
          className="inline-flex items-center gap-1.5 rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px transition-all"
        >
          <Plus className="size-4" /> Add Credential
        </button>
      </div>

      <div
        role="note"
        className="flex gap-2 bg-[rgba(59,130,246,0.06)] border border-[rgba(59,130,246,0.15)] rounded-[10px] px-3 py-2.5 text-xs text-[var(--text-secondary)]"
      >
        <Info className="size-4 shrink-0 mt-0.5 text-[var(--running)]" aria-hidden />
        <div>
          <p className="font-medium text-[var(--text-primary)]">ghcr.io requires a Classic PAT</p>
          <p className="mt-0.5">
            Fine-grained PATs can clone private repositories but <strong className="text-[var(--text-primary)]">cannot</strong> authenticate
            to GitHub Container Registry (ghcr.io). Use a Classic PAT with{' '}
            <code className="font-[var(--font-mono)] text-[11px] bg-[rgba(59,130,246,0.1)] px-1.5 py-0.5 rounded">read:packages</code> scope when linking a credential to a Registry entry.
          </p>
        </div>
      </div>

      {showForm && (
        <CredentialForm
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
      ) : credentials.length === 0 && !showForm ? (
        <p className="text-sm text-[var(--text-secondary)] text-center py-8">
          No credentials configured.
        </p>
      ) : (
        <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] overflow-hidden">
          <table className="w-full text-sm border-collapse">
            <thead>
              <tr className="border-b border-[rgba(255,255,255,0.06)] text-left">
                <th className="text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] px-4 py-3">Name</th>
                <th className="text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] px-4 py-3">Username</th>
                <th className="text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)] px-4 py-3">Created</th>
                <th className="px-4 py-3 sr-only">Actions</th>
              </tr>
            </thead>
            <tbody>
              {credentials.map(cred => (
                <tr key={cred.id} className="border-b border-[rgba(255,255,255,0.06)] hover:bg-[var(--accent)] transition-colors">
                  <td className="px-4 py-3 font-medium">{cred.name}</td>
                  <td className="px-4 py-3 font-[var(--font-mono)] text-xs text-[var(--text-tertiary)]">{cred.username}</td>
                  <td className="px-4 py-3 text-[var(--text-tertiary)]">
                    {new Date(cred.createdAt).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3">
                    <button
                      onClick={() => {
                        if (confirm(`Delete credential "${cred.name}"?`)) remove.mutate(cred.id)
                      }}
                      aria-label={`Delete ${cred.name}`}
                      className="size-7 flex items-center justify-center rounded-md hover:bg-[var(--danger-bg)] text-[var(--danger)] transition-colors"
                    >
                      <Trash2 className="size-4" />
                    </button>
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

function CredentialForm({
  onSubmit,
  saving,
  error,
  onCancel,
}: {
  onSubmit: (data: CreateCredentialRequest) => void
  saving: boolean
  error: string | null
  onCancel: () => void
}) {
  const [form, setForm] = useState<CreateCredentialRequest>({
    name: '',
    username: '',
    token: '',
  })

  function handle(e: React.ChangeEvent<HTMLInputElement>) {
    setForm(prev => ({ ...prev, [e.target.name]: e.target.value }))
  }

  return (
    <form
      onSubmit={e => { e.preventDefault(); onSubmit(form) }}
      className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 space-y-4 animate-[wt-card-in_0.4s_ease-out_both]"
    >
      <h2 className="text-sm font-semibold">New Credential</h2>
      <div className="grid grid-cols-2 gap-3">
        <div className="space-y-1">
          <label className="text-xs font-semibold text-[var(--text-secondary)]">Name <span className="text-[var(--danger)]">*</span></label>
          <input name="name" value={form.name} onChange={handle} required className="input w-full" placeholder="GitHub (repo clone)" />
        </div>
        <div className="space-y-1">
          <label className="text-xs font-semibold text-[var(--text-secondary)]">Username <span className="text-[var(--danger)]">*</span></label>
          <input name="username" value={form.username} onChange={handle} required className="input w-full" placeholder="your-github-username" />
        </div>
        <div className="col-span-2 space-y-1">
          <label className="text-xs font-semibold text-[var(--text-secondary)]">Token <span className="text-[var(--danger)]">*</span></label>
          <input name="token" value={form.token} onChange={handle} required type="password" className="input w-full font-mono" placeholder="ghp_… or github_pat_…" />
        </div>
      </div>
      {error && <p role="alert" className="text-xs text-[var(--danger)]">{error}</p>}
      <div className="flex gap-2">
        <button type="submit" disabled={saving} className="rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-4 py-2 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px disabled:opacity-50 transition-all">
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button type="button" onClick={onCancel} className="rounded-[10px] border border-[var(--border)] px-4 py-2 text-[13px] font-medium text-[var(--text-secondary)] hover:bg-[var(--accent)] hover:text-[var(--text-primary)] transition-all">
          Cancel
        </button>
      </div>
    </form>
  )
}
