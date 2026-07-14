import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link, useNavigate } from '@tanstack/react-router'
import { ChevronLeft, ExternalLink, PlayCircle, Plus, Trash2, Users } from 'lucide-react'
import { api } from '@/lib/api'
import type { Tenant, TemplateEnvVarInput } from '@/lib/types'
import { timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { EnvVarEditor } from '@/components/env-var-editor'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { SectionHeader } from '@/components/ui/section-header'
import { Spinner } from '@/components/ui/spinner'
import { StatusBadge } from '@/components/ui/status-badge'
import { toast } from '@/components/ui/use-toast'

const routeApi = getRouteApi('/templates/$id')

export function TemplateDetailPage() {
  const { id } = routeApi.useParams()
  const templateId = Number(id)
  const qc = useQueryClient()
  const navigate = useNavigate()

  const [slug, setSlug] = useState('')
  const [showOverrides, setShowOverrides] = useState(false)
  const [overrides, setOverrides] = useState<TemplateEnvVarInput[]>([{ key: '', value: '' }])
  const [confirmDeleteTemplate, setConfirmDeleteTemplate] = useState(false)

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['template', templateId],
    queryFn: () => api.templates.get(templateId),
  })

  const { data: tenants = [] } = useQuery({
    queryKey: ['tenants', templateId],
    queryFn: () => api.templates.listTenants(templateId),
    refetchInterval: (q) =>
      (q.state.data ?? []).some((t) => t.lastDeployStatus === 'running' || t.lastDeployStatus === 'queued')
        ? 2000
        : false,
  })

  const addTenant = useMutation({
    mutationFn: () => {
      const envOverrides = overrides.filter((v) => v.key.trim() !== '')
      return api.templates.addTenant({
        templateId,
        slug,
        envOverrides: envOverrides.length > 0 ? envOverrides : null,
      })
    },
    onSuccess: (t) => {
      toast.success(`Tenant ${t.tenantSlug} created — deploying…`)
      setSlug('')
      setOverrides([{ key: '', value: '' }])
      setShowOverrides(false)
      qc.invalidateQueries({ queryKey: ['tenants', templateId] })
      qc.invalidateQueries({ queryKey: ['template', templateId] })
    },
    onError: (err: Error) => toast.error(err.message),
  })

  const deployAll = useMutation({
    mutationFn: () => api.templates.deployAll(templateId),
    onSuccess: (count) => {
      toast.info(`Deploying ${count} tenant${count === 1 ? '' : 's'}…`)
      qc.invalidateQueries({ queryKey: ['tenants', templateId] })
    },
    onError: (err: Error) => toast.error(err.message),
  })

  const removeTemplate = useMutation({
    mutationFn: () => api.templates.delete(templateId),
    onSuccess: () => {
      toast.success('Template deleted.')
      qc.invalidateQueries({ queryKey: ['templates'] })
      navigate({ to: '/templates' })
    },
    onError: (err: Error) => toast.error(err.message),
    onSettled: () => setConfirmDeleteTemplate(false),
  })

  if (isLoading) return <div className="flex justify-center p-10"><Spinner /></div>
  if (isError || !data)
    return (
      <div className="mx-auto max-w-[900px] p-6">
        <Banner tone="danger" title="Couldn’t load template">
          {(error as Error)?.message ?? 'Not found.'}
        </Banner>
      </div>
    )

  const { template, baseEnvVars } = data

  const columns: DataListColumn<Tenant>[] = [
    {
      key: 'slug',
      header: 'Tenant',
      cell: (t) => (
        <Link
          to="/stacks/$id"
          params={{ id: String(t.stackId) }}
          className="font-medium text-text hover:text-brand"
        >
          {t.tenantSlug}
        </Link>
      ),
    },
    {
      key: 'domain',
      header: 'Domain',
      cell: (t) =>
        t.domain ? (
          <a
            href={`https://${t.domain}`}
            target="_blank"
            rel="noreferrer"
            className="inline-flex items-center gap-1.5 font-mono text-[13px] text-text-2 hover:text-brand"
          >
            {t.domain}
            <ExternalLink className="size-3.5 text-text-3" />
          </a>
        ) : (
          <span className="text-text-3">—</span>
        ),
    },
    {
      key: 'status',
      header: 'Status',
      cell: (t) => <StatusBadge status={t.lastDeployStatus} />,
    },
    {
      key: 'deployed',
      header: 'Last deployed',
      cell: (t) =>
        t.lastDeployedAt ? (
          <span className="tnum text-[13px] text-text-2">{timeAgo(t.lastDeployedAt)}</span>
        ) : (
          <span className="text-text-3">never</span>
        ),
    },
  ]

  return (
    <div className="mx-auto max-w-[1000px] space-y-6 p-4 md:p-6">
      <Link
        to="/templates"
        className="inline-flex items-center gap-1 text-[13px] text-text-2 transition-colors hover:text-text"
      >
        <ChevronLeft className="size-4" /> Templates
      </Link>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="inline-flex items-center gap-2 text-[24px] font-semibold tracking-[-0.02em]">
          {template.name}
          <Badge tone={template.instanceCount > 0 ? 'brand' : 'neutral'}>
            <Users className="mr-1 size-3" /> {template.instanceCount}
          </Badge>
        </h1>
        <div className="flex items-center gap-2">
          <Button
            variant="secondary"
            loading={deployAll.isPending}
            disabled={tenants.length === 0}
            onClick={() => deployAll.mutate()}
          >
            <PlayCircle /> Deploy all
          </Button>
          <Button
            variant="ghost"
            className="text-text-2 hover:text-danger"
            onClick={() => setConfirmDeleteTemplate(true)}
          >
            <Trash2 /> Delete
          </Button>
        </div>
      </div>

      <Card>
        <CardContent className="space-y-1 pt-5 text-[13px] text-text-2">
          <p className="font-mono">{template.repositoryUrl} · {template.branch}</p>
          <p className="font-mono">{template.domainPattern} → {template.targetServiceName}:{template.targetPort}</p>
          <p>{baseEnvVars.length} base env var{baseEnvVars.length === 1 ? '' : 's'}</p>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="pt-5">
          <SectionHeader title="Add tenant" description="Spins up an isolated copy on its own subdomain." />
          <div className="space-y-4">
            <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
              <Field label="Tenant slug" required hint={template.domainPattern.replace('{tenant}', slug || 'slug')}>
                {({ id: fid, describedBy }) => (
                  <Input
                    id={fid}
                    aria-describedby={describedBy}
                    mono
                    value={slug}
                    onChange={(e) => setSlug(e.target.value)}
                    placeholder="tenant1"
                    autoComplete="off"
                    spellCheck={false}
                    className="sm:w-64"
                  />
                )}
              </Field>
              <Button
                loading={addTenant.isPending}
                disabled={!slug.trim()}
                onClick={() => addTenant.mutate()}
              >
                <Plus /> Add tenant
              </Button>
            </div>
            <button
              type="button"
              className="text-[13px] text-text-2 underline-offset-2 hover:text-text hover:underline"
              onClick={() => setShowOverrides((v) => !v)}
            >
              {showOverrides ? 'Hide' : 'Add'} environment overrides
            </button>
            {showOverrides && <EnvVarEditor value={overrides} onChange={setOverrides} />}
          </div>
        </CardContent>
      </Card>

      <DataList
        items={tenants}
        getKey={(t) => t.stackId}
        columns={columns}
        renderCard={(t) => (
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-3">
              <Link to="/stacks/$id" params={{ id: String(t.stackId) }} className="font-medium text-text hover:text-brand">
                {t.tenantSlug}
              </Link>
              <StatusBadge status={t.lastDeployStatus} />
            </div>
            {t.domain && <p className="font-mono text-[13px] text-text-2">{t.domain}</p>}
          </div>
        )}
        emptyState={
          <EmptyState icon={Users} title="No tenants yet" description="Add your first tenant above." />
        }
        aria-label="Tenants"
      />

      <ConfirmDialog
        open={confirmDeleteTemplate}
        onOpenChange={(open) => {
          if (!open && !removeTemplate.isPending) setConfirmDeleteTemplate(false)
        }}
        title={`Delete ${template.name}?`}
        description="Existing tenants keep running; they're just detached from this template."
        confirmLabel="Delete"
        tone="danger"
        loading={removeTemplate.isPending}
        onConfirm={() => removeTemplate.mutate()}
      />
    </div>
  )
}
