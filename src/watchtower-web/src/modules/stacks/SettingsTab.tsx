import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { api } from '@/lib/api'
import { apiBase } from '@/lib/config'
import type { Stack, StackEnvVarInput, UpdateStackRequest } from '@/lib/types'
import { EnvVarEditor } from '@/components/env-var-editor'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { CopyButton } from '@/components/ui/copy-button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { SecretField } from '@/components/ui/secret-field'
import { SectionHeader } from '@/components/ui/section-header'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'

const NO_CREDENTIAL = 'none'

function webhookUrl(stackId: number): string {
  const base = apiBase || (typeof window !== 'undefined' ? window.location.origin : '')
  return `${base}/api/webhooks/stacks/${stackId}/deploy`
}

export function SettingsTab({ stack }: { stack: Stack }) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const stackId = stack.id

  const { data: envVars = [] } = useQuery({
    queryKey: ['stacks', stackId, 'env'],
    queryFn: () => api.stacks.getEnv(stackId),
  })

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

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

            <Field label="Branch" hint="Defaults to main">
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
