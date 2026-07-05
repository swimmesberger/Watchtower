import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { ChevronLeft } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateStackRequest, StackEnvVarInput } from '@/lib/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { SectionHeader } from '@/components/ui/section-header'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { SecretField } from '@/components/ui/secret-field'
import { Banner } from '@/components/ui/banner'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { EnvVarEditor } from '@/components/env-var-editor'
import { toast } from '@/components/ui/use-toast'
import { randomUuid } from '@/lib/utils'

const NO_CREDENTIAL = 'none'

/** Two random UUIDs, hyphens stripped — the same recipe used in stack settings (A12). */
function generateWebhookToken() {
  return (randomUuid() + randomUuid()).replaceAll('-', '')
}

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
    webhookEnabled: false,
  })
  const [envDraft, setEnvDraft] = useState<StackEnvVarInput[]>([{ key: '', value: '' }])
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: (data: CreateStackRequest) => api.stacks.create(data),
    onSuccess: (stack) => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      toast.success(`Stack ${stack.name} created.`)
      navigate({ to: '/stacks/$id', params: { id: String(stack.id) } })
    },
    onError: (err: Error) => {
      setError(err.message)
      toast.error(err.message)
    },
  })

  function field<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    const envVars = envDraft.filter((v) => v.key.trim() !== '')
    create.mutate({
      name: form.name,
      repositoryUrl: form.repositoryUrl,
      composeFilePath: form.composeFilePath,
      branch: form.branch,
      composeProjectName: form.composeProjectName || null,
      credentialId: form.credentialId,
      webhookEnabled: form.webhookEnabled,
      webhookToken: form.webhookEnabled ? form.webhookToken || null : null,
      ...(envVars.length > 0 ? { envVars } : {}),
    })
  }

  return (
    <div className="mx-auto max-w-[720px]">
      <Link
        to="/stacks"
        className="inline-flex items-center gap-1 text-[13px] text-text-2 transition-colors hover:text-text"
      >
        <ChevronLeft className="size-4" />
        Stacks
      </Link>

      <h1 className="mt-3 text-2xl font-semibold tracking-tight text-text">New stack</h1>
      <p className="mt-1 text-[13px] text-text-2">
        Point Watchtower at a git repository with a compose file to deploy.
      </p>

      <form onSubmit={submit} className="mt-6 space-y-6">
        {/* ── Repository ── */}
        <Card>
          <CardContent className="pt-5">
            <SectionHeader title="Repository" />

            <div className="space-y-4">
              <Field label="Stack name" required>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    name="name"
                    value={form.name}
                    onChange={(e) => field('name', e.target.value)}
                    required
                    placeholder="web-app"
                    autoComplete="off"
                  />
                )}
              </Field>

              <Field label="Repository URL" required>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    mono
                    name="repositoryUrl"
                    value={form.repositoryUrl}
                    onChange={(e) => field('repositoryUrl', e.target.value)}
                    required
                    placeholder="https://github.com/owner/repo"
                    autoComplete="off"
                    spellCheck={false}
                  />
                )}
              </Field>

              <div className="grid gap-4 md:grid-cols-2">
                <Field
                  label="Compose file path"
                  hint="Relative to the repo root, e.g. docker-compose.yml"
                >
                  {({ id, describedBy }) => (
                    <Input
                      id={id}
                      aria-describedby={describedBy}
                      mono
                      name="composeFilePath"
                      value={form.composeFilePath}
                      onChange={(e) => field('composeFilePath', e.target.value)}
                      placeholder="docker-compose.yml"
                      autoComplete="off"
                      spellCheck={false}
                    />
                  )}
                </Field>

                <Field label="Branch" hint="Defaults to main">
                  {({ id, describedBy }) => (
                    <Input
                      id={id}
                      aria-describedby={describedBy}
                      mono
                      name="branch"
                      value={form.branch}
                      onChange={(e) => field('branch', e.target.value)}
                      placeholder="main"
                      autoComplete="off"
                      spellCheck={false}
                    />
                  )}
                </Field>
              </div>

              <Field label="Compose project name" hint="Defaults to the stack name">
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    mono
                    name="composeProjectName"
                    value={form.composeProjectName ?? ''}
                    onChange={(e) => field('composeProjectName', e.target.value)}
                    placeholder={form.name || 'web-app'}
                    autoComplete="off"
                    spellCheck={false}
                  />
                )}
              </Field>
            </div>
          </CardContent>
        </Card>

        {/* ── Authentication ── */}
        <Card>
          <CardContent className="pt-5">
            <SectionHeader
              title="Authentication"
              description="Only needed for private repos or registries."
            />

            <div className="space-y-4">
              <Field
                label="Credential"
                hint="Only needed for private repositories"
              >
                {({ id, describedBy }) => (
                  <Select
                    value={form.credentialId != null ? String(form.credentialId) : NO_CREDENTIAL}
                    onValueChange={(v) =>
                      field('credentialId', v === NO_CREDENTIAL ? null : Number(v))
                    }
                  >
                    <SelectTrigger id={id} aria-describedby={describedBy}>
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
                )}
              </Field>

              {/* Webhook (A12) */}
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <Label htmlFor="webhook-enabled">Enable webhook</Label>
                  <p className="mt-1 text-xs text-text-3">
                    Expose a deploy webhook your CI can call after each push.
                  </p>
                </div>
                <Switch
                  id="webhook-enabled"
                  checked={form.webhookEnabled ?? false}
                  onCheckedChange={(on) => field('webhookEnabled', on)}
                />
              </div>

              {form.webhookEnabled && (
                <Field
                  label="Webhook token"
                  hint="Sent as a Bearer token by your CI. Leave blank to allow unauthenticated deploys (not recommended)."
                >
                  <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                    <SecretField
                      value={form.webhookToken ?? ''}
                      onChange={(v) => field('webhookToken', v)}
                      placeholder="Leave blank for unauthenticated deploys"
                      aria-label="Webhook token"
                      className="flex-1"
                    />
                    <Button
                      type="button"
                      variant="secondary"
                      size="md"
                      onClick={() => field('webhookToken', generateWebhookToken())}
                      className="shrink-0"
                    >
                      Generate
                    </Button>
                  </div>
                </Field>
              )}
            </div>
          </CardContent>
        </Card>

        {/* ── Environment variables ── */}
        <Card>
          <CardContent className="pt-5">
            <SectionHeader title="Environment variables" />

            <EnvVarEditor value={envDraft} onChange={setEnvDraft} />

            <p className="mt-3 text-xs text-text-3">
              Written to an <code className="font-mono text-text-2">--env-file</code> on every
              deploy and interpolated into the compose file as{' '}
              <code className="font-mono text-text-2">${'{KEY}'}</code>.
            </p>
          </CardContent>
        </Card>

        {error && (
          <Banner tone="danger" title="Could not create stack">
            {error}
          </Banner>
        )}

        {/* ── Desktop footer ── */}
        <div className="hidden justify-end gap-3 md:flex">
          <Button asChild variant="secondary">
            <Link to="/stacks">Cancel</Link>
          </Button>
          <Button type="submit" loading={create.isPending}>
            Create stack
          </Button>
        </div>

        {/* ── Mobile sticky primary action, above the bottom tab bar (§6) ── */}
        <div className="fixed inset-x-0 bottom-bottombar z-20 border-t border-border bg-surface/95 p-4 backdrop-blur md:hidden">
          <Button type="submit" loading={create.isPending} className="w-full">
            Create stack
          </Button>
        </div>
      </form>

      {/* Spacer so the mobile sticky bar never overlaps the last card. */}
      <div className="h-20 md:hidden" aria-hidden />
    </div>
  )
}
