import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { ChevronLeft } from 'lucide-react'
import { api } from '@/lib/api'
import type { TemplateEnvVarInput } from '@/lib/types'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { EnvVarEditor } from '@/components/env-var-editor'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { SectionHeader } from '@/components/ui/section-header'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { toast } from '@/components/ui/use-toast'

const NO_CREDENTIAL = 'none'

export function TemplateNewPage() {
  const qc = useQueryClient()
  const navigate = useNavigate()

  const { data: credentials = [] } = useQuery({ queryKey: ['credentials'], queryFn: api.credentials.list })

  const [form, setForm] = useState({
    name: '',
    repositoryUrl: '',
    composeFilePath: 'docker-compose.yml',
    branch: 'main',
    credentialId: null as number | null,
    domainPattern: '{tenant}.example.com',
    targetServiceName: 'web',
    targetPort: '3000',
  })
  const [envDraft, setEnvDraft] = useState<TemplateEnvVarInput[]>([{ key: '', value: '' }])
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: () => {
      const baseEnvVars = envDraft.filter((v) => v.key.trim() !== '')
      return api.templates.create({
        name: form.name,
        repositoryUrl: form.repositoryUrl,
        composeFilePath: form.composeFilePath,
        branch: form.branch,
        credentialId: form.credentialId,
        domainPattern: form.domainPattern,
        targetServiceName: form.targetServiceName,
        targetPort: Number(form.targetPort),
        baseEnvVars: baseEnvVars.length > 0 ? baseEnvVars : null,
      })
    },
    onSuccess: (t) => {
      qc.invalidateQueries({ queryKey: ['templates'] })
      toast.success(`Template ${t.name} created.`)
      navigate({ to: '/templates/$id', params: { id: String(t.id) } })
    },
    onError: (err: Error) => {
      setError(err.message)
      toast.error(err.message)
    },
  })

  function set<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    if (!form.domainPattern.includes('{tenant}')) {
      setError('Domain pattern must contain {tenant}.')
      return
    }
    create.mutate()
  }

  return (
    <div className="mx-auto max-w-[720px]">
      <Link
        to="/templates"
        className="inline-flex items-center gap-1 text-[13px] text-text-2 transition-colors hover:text-text"
      >
        <ChevronLeft className="size-4" />
        Templates
      </Link>

      <h1 className="mt-3 text-2xl font-semibold tracking-tight text-text">New template</h1>
      <p className="mt-1 text-[13px] text-text-2">
        A template is deployed once per tenant, each on its own subdomain, fully isolated.
      </p>

      <form onSubmit={submit} className="mt-6 space-y-6">
        <Card>
          <CardContent className="pt-5">
            <SectionHeader title="Source" />
            <div className="space-y-4">
              <Field label="Template name" required>
                {({ id, describedBy }) => (
                  <Input id={id} aria-describedby={describedBy} value={form.name}
                    onChange={(e) => set('name', e.target.value)} required placeholder="saas-app"
                    autoComplete="off" />
                )}
              </Field>
              <Field label="Repository URL" required>
                {({ id, describedBy }) => (
                  <Input id={id} aria-describedby={describedBy} mono value={form.repositoryUrl}
                    onChange={(e) => set('repositoryUrl', e.target.value)} required
                    placeholder="https://github.com/owner/repo" autoComplete="off" spellCheck={false} />
                )}
              </Field>
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Compose file path">
                  {({ id, describedBy }) => (
                    <Input id={id} aria-describedby={describedBy} mono value={form.composeFilePath}
                      onChange={(e) => set('composeFilePath', e.target.value)}
                      placeholder="docker-compose.yml" autoComplete="off" spellCheck={false} />
                  )}
                </Field>
                <Field label="Branch">
                  {({ id, describedBy }) => (
                    <Input id={id} aria-describedby={describedBy} mono value={form.branch}
                      onChange={(e) => set('branch', e.target.value)} placeholder="main"
                      autoComplete="off" spellCheck={false} />
                  )}
                </Field>
              </div>
              <Field label="Credential" hint="Only needed for private repositories">
                {({ id, describedBy }) => (
                  <Select
                    value={form.credentialId != null ? String(form.credentialId) : NO_CREDENTIAL}
                    onValueChange={(v) => set('credentialId', v === NO_CREDENTIAL ? null : Number(v))}
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
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent className="pt-5">
            <SectionHeader title="Routing" description="Each tenant gets a subdomain routed to one service." />
            <div className="space-y-4">
              <Field label="Domain pattern" required hint="Use {tenant} where the tenant slug goes">
                {({ id, describedBy }) => (
                  <Input id={id} aria-describedby={describedBy} mono value={form.domainPattern}
                    onChange={(e) => set('domainPattern', e.target.value)} required
                    placeholder="{tenant}.example.com" autoComplete="off" spellCheck={false} />
                )}
              </Field>
              <div className="grid grid-cols-2 gap-4">
                <Field label="Target service" required>
                  {({ id, describedBy }) => (
                    <Input id={id} aria-describedby={describedBy} mono value={form.targetServiceName}
                      onChange={(e) => set('targetServiceName', e.target.value)} required
                      placeholder="web" autoComplete="off" spellCheck={false} />
                  )}
                </Field>
                <Field label="Target port" required>
                  {({ id, describedBy }) => (
                    <Input id={id} aria-describedby={describedBy} mono type="number" min={1} max={65535}
                      value={form.targetPort} onChange={(e) => set('targetPort', e.target.value)} required />
                  )}
                </Field>
              </div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent className="pt-5">
            <SectionHeader title="Base environment variables" description="Shared defaults; each tenant can override." />
            <EnvVarEditor value={envDraft} onChange={setEnvDraft} />
          </CardContent>
        </Card>

        {error && <Banner tone="danger" title="Could not create template">{error}</Banner>}

        <div className="flex justify-end gap-3">
          <Button asChild variant="secondary">
            <Link to="/templates">Cancel</Link>
          </Button>
          <Button type="submit" loading={create.isPending}>
            Create template
          </Button>
        </div>
      </form>
    </div>
  )
}
