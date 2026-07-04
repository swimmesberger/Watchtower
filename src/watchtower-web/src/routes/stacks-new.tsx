import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { Eye, EyeOff, Plus, Trash2 } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateStackRequest, StackEnvVarInput } from '@/lib/types'

export function StackNewPage() {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const [form, setForm] = useState<Omit<CreateStackRequest, 'envVars'>>({
    name: '',
    repositoryUrl: '',
    composeFilePath: 'docker-compose.yml',
    branch: 'main',
    composeProjectName: '',
    credentialId: null,
    webhookToken: '',
  })
  const [envDraft, setEnvDraft] = useState<StackEnvVarInput[]>([{ key: '', value: '' }])
  const [envVisible, setEnvVisible] = useState<Set<number>>(new Set())
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: (data: CreateStackRequest) => api.stacks.create(data),
    onSuccess: stack => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      navigate({ to: '/stacks/$id', params: { id: String(stack.id) } })
    },
    onError: (err: Error) => setError(err.message),
  })

  function handle(e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) {
    const { name, value } = e.target
    setForm(prev => ({
      ...prev,
      [name]: name === 'credentialId' ? (value ? Number(value) : null) : value,
    }))
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

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    const envVars = envDraft.filter(v => v.key.trim() !== '')
    create.mutate({
      ...form,
      composeProjectName: form.composeProjectName || null,
      webhookToken: form.webhookToken || null,
      ...(envVars.length > 0 ? { envVars } : {}),
    })
  }

  return (
    <div className="p-6 space-y-6 max-w-2xl">
      <div>
        <a
          href="/stacks"
          onClick={e => { e.preventDefault(); navigate({ to: '/stacks' }) }}
          className="inline-flex items-center gap-1.5 text-xs text-[var(--text-tertiary)] hover:text-[var(--text-primary)] transition-colors"
        >
          ← Back to Stacks
        </a>
        <h1 className="text-[22px] font-bold tracking-tight mt-3">New Stack</h1>
        <p className="text-[13px] text-[var(--text-tertiary)] mt-0.5">Configure and deploy a new compose stack</p>
      </div>

      <form onSubmit={submit} className="space-y-6">
        <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 space-y-4">
          <h2 className="text-sm font-semibold">Repository</h2>

          <Field label="Stack Name" required>
            <input
              name="name"
              value={form.name}
              onChange={handle}
              required
              placeholder="my-app"
              className="input w-full text-[13px]"
            />
          </Field>

          <Field label="Repository URL" required>
            <input
              name="repositoryUrl"
              value={form.repositoryUrl}
              onChange={handle}
              required
              placeholder="https://github.com/owner/repo"
              className="input w-full text-[13px]"
            />
          </Field>

          <Field label="Compose File Path">
            <input
              name="composeFilePath"
              value={form.composeFilePath}
              onChange={handle}
              placeholder="docker-compose.yml"
              className="input w-full text-[13px]"
            />
          </Field>

          <Field label="Branch">
            <input
              name="branch"
              value={form.branch}
              onChange={handle}
              placeholder="main"
              className="input w-full text-[13px]"
            />
          </Field>

          <Field label="Compose Project Name" hint="Defaults to stack name if empty">
            <input
              name="composeProjectName"
              value={form.composeProjectName ?? ''}
              onChange={handle}
              placeholder="my-app"
              className="input w-full text-[13px]"
            />
          </Field>
        </div>

        <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 space-y-4">
          <h2 className="text-sm font-semibold">Authentication</h2>

          <Field label="Credential">
            <select
              name="credentialId"
              value={form.credentialId ?? ''}
              onChange={handle}
              className="input w-full text-[13px]"
            >
              <option value="">None (public repository)</option>
              {credentials.map(c => (
                <option key={c.id} value={c.id}>
                  {c.name} ({c.username})
                </option>
              ))}
            </select>
          </Field>

          <Field label="Webhook Token" hint="Optional bearer token to protect the deploy webhook">
            <input
              name="webhookToken"
              value={form.webhookToken ?? ''}
              onChange={handle}
              type="password"
              placeholder="Leave blank to disable webhook"
              className="input w-full text-[13px]"
            />
          </Field>
        </div>

        {/* Environment Variables */}
        <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] p-5 space-y-4">
          <h2 className="text-sm font-semibold">Environment Variables</h2>
          <div className="rounded-[10px] border border-[rgba(255,255,255,0.06)] overflow-hidden">
            <div className="grid grid-cols-[1fr_1fr_2.5rem] bg-[rgba(255,255,255,0.02)] px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.06em] text-[var(--text-tertiary)]">
              <span>Key</span>
              <span>Value</span>
              <span />
            </div>
            <div>
              {envDraft.map((row, i) => {
                const isBlankTrailer = i === envDraft.length - 1
                return (
                  <div key={i} className="grid grid-cols-[1fr_1fr_2.5rem] items-center border-b border-[rgba(255,255,255,0.06)] last:border-b-0">
                    <input
                      value={row.key}
                      onChange={e => updateEnvRow(i, 'key', e.target.value)}
                      placeholder={isBlankTrailer ? 'NEW_KEY' : ''}
                      spellCheck={false}
                      className="font-[var(--font-mono)] bg-transparent border-0 focus:ring-0 text-[13px] px-3 py-2 focus:outline-none placeholder:text-[var(--text-tertiary)] w-full border-r border-[rgba(255,255,255,0.06)]"
                      aria-label={`Key for variable ${i + 1}`}
                    />
                    <div className="flex items-center border-r border-[rgba(255,255,255,0.06)] relative">
                      <input
                        value={row.value}
                        onChange={e => updateEnvRow(i, 'value', e.target.value)}
                        placeholder={isBlankTrailer ? 'value' : ''}
                        spellCheck={false}
                        type={envVisible.has(i) ? 'text' : 'password'}
                        className="font-[var(--font-mono)] bg-transparent border-0 focus:ring-0 text-[13px] px-3 py-2 focus:outline-none placeholder:text-[var(--text-tertiary)] w-full pr-8"
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
                          className="rounded p-1 hover:bg-[var(--danger-bg)] text-[var(--danger)] transition-colors"
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
          </p>
        </div>

        {error && (
          <p role="alert" className="text-[var(--danger)] text-xs">
            {error}
          </p>
        )}

        <div className="flex gap-3 pt-2">
          <button
            type="submit"
            disabled={create.isPending}
            className="rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-5 py-2.5 text-[13px] font-semibold hover:shadow-[0_0_20px_var(--accent-glow)] hover:-translate-y-px disabled:opacity-50 transition-all"
          >
            {create.isPending ? 'Creating…' : 'Create Stack'}
          </button>
          <a
            href="/stacks"
            onClick={e => { e.preventDefault(); navigate({ to: '/stacks' }) }}
            className="rounded-[10px] border border-[var(--border)] px-4 py-2 text-[13px] font-medium text-[var(--text-secondary)] hover:bg-[var(--accent)] hover:text-[var(--text-primary)] transition-all"
          >
            Cancel
          </a>
        </div>
      </form>
    </div>
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

