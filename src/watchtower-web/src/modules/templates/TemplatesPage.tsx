import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Layers, Plus, Trash2, Users } from 'lucide-react'
import { api } from '@/lib/api'
import type { StackTemplate } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

export function TemplatesPage() {
  const qc = useQueryClient()
  const [pendingDelete, setPendingDelete] = useState<StackTemplate | null>(null)

  const { data: templates = [], isLoading, isError, error, refetch } = useQuery({
    queryKey: ['templates'],
    queryFn: api.templates.list,
  })

  const remove = useMutation({
    mutationFn: (t: StackTemplate) => api.templates.delete(t.id),
    onSuccess: (_d, t) => {
      toast.success(`Deleted template ${t.name}.`)
      qc.invalidateQueries({ queryKey: ['templates'] })
    },
    onError: (err: Error, t) => toast.error(`Failed to delete ${t.name}: ${err.message}`),
    onSettled: () => setPendingDelete(null),
  })

  const columns: DataListColumn<StackTemplate>[] = [
    {
      key: 'name',
      header: 'Name',
      cell: (t) => (
        <Link
          to="/templates/$id"
          params={{ id: String(t.id) }}
          className="inline-flex items-center gap-2 font-medium text-text hover:text-brand"
        >
          <Layers className="size-4 text-text-3" />
          {t.name}
        </Link>
      ),
    },
    {
      key: 'domain',
      header: 'Domain pattern',
      cell: (t) => <span className="font-mono text-[13px] text-text-2">{t.domainPattern}</span>,
    },
    {
      key: 'target',
      header: 'Target',
      cell: (t) => (
        <span className="font-mono text-[13px] text-text-2">
          {t.targetServiceName}:{t.targetPort}
        </span>
      ),
    },
    {
      key: 'tenants',
      header: 'Tenants',
      cell: (t) => (
        <Badge tone={t.instanceCount > 0 ? 'brand' : 'neutral'}>
          <Users className="mr-1 size-3" /> {t.instanceCount}
        </Badge>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (t) => (
        <Tooltip label="Delete template">
          <Button
            size="icon-sm"
            variant="ghost"
            aria-label={`Delete ${t.name}`}
            onClick={() => setPendingDelete(t)}
            className="text-text-2 hover:text-danger"
          >
            <Trash2 />
          </Button>
        </Tooltip>
      ),
    },
  ]

  const renderCard = (t: StackTemplate) => (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3">
        <Link
          to="/templates/$id"
          params={{ id: String(t.id) }}
          className="inline-flex items-center gap-2 font-medium text-text hover:text-brand"
        >
          <Layers className="size-4 text-text-3" />
          {t.name}
        </Link>
        <Badge tone={t.instanceCount > 0 ? 'brand' : 'neutral'}>
          <Users className="mr-1 size-3" /> {t.instanceCount}
        </Badge>
      </div>
      <p className="font-mono text-[13px] text-text-2">
        {t.domainPattern} → {t.targetServiceName}:{t.targetPort}
      </p>
    </div>
  )

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-4 md:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Templates</h1>
        <Button asChild variant="primary">
          <Link to="/templates/new">
            <Plus /> New template
          </Link>
        </Button>
      </div>

      {isError && (
        <Banner
          tone="danger"
          title="Couldn’t load templates"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          {(error as Error)?.message ?? 'An unexpected error occurred.'}
        </Banner>
      )}

      {!isError && (
        <DataList
          items={templates}
          getKey={(t) => t.id}
          columns={columns}
          renderCard={renderCard}
          skeletonRows={isLoading ? 4 : undefined}
          emptyState={
            <EmptyState
              icon={Layers}
              title="No templates yet"
              description="Create a template to run the same stack for many tenants, each on its own subdomain."
              action={
                <Button asChild variant="primary">
                  <Link to="/templates/new">
                    <Plus /> New template
                  </Link>
                </Button>
              }
            />
          }
          aria-label="Templates"
        />
      )}

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.name}?` : 'Delete template?'}
        description="Existing tenants keep running; they're just detached from this template."
        confirmLabel="Delete"
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => {
          if (pendingDelete) remove.mutate(pendingDelete)
        }}
      />
    </div>
  )
}
