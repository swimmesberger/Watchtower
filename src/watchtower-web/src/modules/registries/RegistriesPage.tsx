import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Boxes, MoreHorizontal, Plus, Trash2, Zap } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateRegistryRequest, Credential, DockerConfigStatus, Registry } from '@/lib/types'
import { useMediaQuery } from '@/hooks/use-media-query'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

const NONE = 'none'

export function RegistriesPage() {
  const qc = useQueryClient()
  const isDesktop = useMediaQuery('(min-width: 768px)')

  const registriesQuery = useQuery({
    queryKey: ['registries'],
    queryFn: api.registries.list,
  })
  const registries = registriesQuery.data ?? []

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })
  const { data: dockerConfig } = useQuery({
    queryKey: ['docker-config'],
    queryFn: api.system.dockerConfig,
  })

  const [showForm, setShowForm] = useState(false)
  const [pendingDelete, setPendingDelete] = useState<Registry | null>(null)

  const create = useMutation({
    mutationFn: (data: CreateRegistryRequest) => api.registries.create(data),
    onSuccess: (registry) => {
      qc.invalidateQueries({ queryKey: ['registries'] })
      setShowForm(false)
      toast.success(`Registry ${registry.name} added.`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to add registry.')
    },
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.registries.delete(id),
    onSuccess: (_data, id) => {
      qc.invalidateQueries({ queryKey: ['registries'] })
      const name = registries.find((r) => r.id === id)?.name ?? 'registry'
      toast.success(`Deleted ${name}.`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to delete registry.')
    },
    onSettled: () => setPendingDelete(null),
  })

  const test = useMutation({
    mutationFn: (id: number) => api.registries.test(id),
    onSuccess: (_message, id) => {
      const name = registries.find((r) => r.id === id)?.name ?? 'registry'
      toast.success(`Login to ${name} succeeded.`)
    },
    onError: (err: Error) => {
      toast.error(`Login failed: ${err.message}`)
    },
  })

  const columns: DataListColumn<Registry>[] = [
    {
      key: 'name',
      header: 'Name',
      cell: (r) => <span className="font-medium text-text">{r.name}</span>,
    },
    {
      key: 'url',
      header: 'URL',
      cell: (r) => <span className="font-mono text-[13px] text-text-2">{r.url}</span>,
    },
    {
      key: 'credential',
      header: 'Credential',
      cell: (r) =>
        r.credentialName ? (
          <span className="text-text-2">{r.credentialName}</span>
        ) : (
          <Badge tone="neutral" size="sm">
            None
          </Badge>
        ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (r) => (
        <div className="flex items-center justify-end gap-1">
          <Tooltip label={`Test login to ${r.name}`}>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label={`Test login to ${r.name}`}
              loading={test.isPending && test.variables === r.id}
              onClick={() => test.mutate(r.id)}
            >
              <Zap />
            </Button>
          </Tooltip>
          <Tooltip label={`Delete ${r.name}`}>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label={`Delete ${r.name}`}
              onClick={() => setPendingDelete(r)}
              className="text-text-2 hover:text-danger"
            >
              <Trash2 />
            </Button>
          </Tooltip>
        </div>
      ),
    },
  ]

  const renderCard = (r: Registry) => (
    <div className="flex flex-col gap-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <p className="font-medium text-text">{r.name}</p>
          <p className="mt-0.5 truncate font-mono text-[13px] text-text-2">{r.url}</p>
        </div>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${r.name}`}>
              <MoreHorizontal />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={() => test.mutate(r.id)}>Test login</DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem destructive onSelect={() => setPendingDelete(r)}>
              Delete
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      <div>
        {r.credentialName ? (
          <Badge tone="neutral" size="sm">
            {r.credentialName}
          </Badge>
        ) : (
          <Badge tone="neutral" size="sm">
            None
          </Badge>
        )}
      </div>
    </div>
  )

  function openForm() {
    if (isDesktop) setShowForm((s) => !s)
    else setShowForm(true)
  }

  return (
    <div className="space-y-6">
      {/* Page header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight text-text">Registries</h1>
          <p className="mt-1 text-sm text-text-2">
            Authenticate private image pulls for your stacks.
          </p>
        </div>
        <Button variant="primary" onClick={openForm}>
          <Plus /> Add registry
        </Button>
      </div>

      {/* docker-config detection */}
      {dockerConfig && <DockerConfigBanner status={dockerConfig} />}

      {/* Query load error → danger Banner with Retry (not a toast) */}
      {registriesQuery.isError && (
        <Banner
          tone="danger"
          title="Couldn't load registries"
          action={
            <Button variant="secondary" size="sm" onClick={() => registriesQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {(registriesQuery.error as Error)?.message ?? 'Something went wrong.'}
        </Banner>
      )}

      {/* Desktop inline add form (below the header) */}
      {isDesktop && showForm && (
        <RegistryForm
          credentials={credentials}
          onSubmit={(data) => create.mutate(data)}
          saving={create.isPending}
          onCancel={() => setShowForm(false)}
        />
      )}

      {/* List */}
      {!registriesQuery.isError && (
        <DataList
          items={registries}
          getKey={(r) => r.id}
          columns={columns}
          renderCard={renderCard}
          skeletonRows={registriesQuery.isLoading ? 5 : undefined}
          aria-label="Registries"
          emptyState={
            <EmptyState
              icon={Boxes}
              title="No registries"
              description="Add a registry to authenticate private image pulls."
              action={
                <Button variant="primary" onClick={openForm}>
                  <Plus /> Add registry
                </Button>
              }
            />
          }
        />
      )}

      {/* Mobile add form → Dialog (bottom sheet) */}
      {!isDesktop && (
        <Dialog open={showForm} onOpenChange={setShowForm}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Add registry</DialogTitle>
              <DialogDescription>
                Authenticate private image pulls for your stacks.
              </DialogDescription>
            </DialogHeader>
            <RegistryForm
              credentials={credentials}
              onSubmit={(data) => create.mutate(data)}
              saving={create.isPending}
              onCancel={() => setShowForm(false)}
              inDialog
            />
          </DialogContent>
        </Dialog>
      )}

      {/* Delete confirmation (no typing) */}
      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(o) => {
          if (!o) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.name}?` : 'Delete registry?'}
        description="This removes the registry configuration. Stacks that relied on it may fail to pull private images until it's re-added."
        confirmLabel="Delete"
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => {
          if (pendingDelete) remove.mutate(pendingDelete.id)
        }}
      />
    </div>
  )
}

function DockerConfigBanner({ status }: { status: DockerConfigStatus }) {
  const configDir = status.path.replace(/\/config\.json$/, '')

  if (status.exists) {
    const label =
      status.source === 'default' ? '~/.docker/config.json' : `${status.source} → ${status.path}`
    return (
      <Banner tone="ok" title="Docker config detected">
        Credentials found at <code className="font-mono text-[12px]">{label}</code>. Private
        registry image pulls will use these automatically.
      </Banner>
    )
  }

  const mountFlag =
    status.source === 'WATCHTOWER_DOCKER_CONFIG'
      ? `-v $HOME/.docker:${configDir}:ro`
      : `-v $HOME/.docker:${configDir}:ro\n-e WATCHTOWER_DOCKER_CONFIG=${configDir}`

  return (
    <Banner tone="warn" title="No Docker credentials file found">
      <div className="space-y-2">
        <p>
          Private image pulls will fail unless credentials are available. If Watchtower runs inside
          a container, mount the host Docker config and set the env var:
        </p>
        <pre className="overflow-x-auto whitespace-pre-wrap rounded-md bg-surface-2 px-2.5 py-1.5 font-mono text-[12px] text-text">
          {mountFlag}
        </pre>
        <p>
          You can also use <code className="font-mono text-[12px]">DOCKER_CONFIG</code> if that env
          var is already set on the host.
          {status.source !== 'default' && (
            <>
              {' '}
              Currently configured at <code className="font-mono text-[12px]">{status.path}</code>{' '}
              but the file does not exist.
            </>
          )}
        </p>
      </div>
    </Banner>
  )
}

function RegistryForm({
  credentials,
  onSubmit,
  saving,
  onCancel,
  inDialog,
}: {
  credentials: Credential[]
  onSubmit: (data: CreateRegistryRequest) => void
  saving: boolean
  onCancel: () => void
  inDialog?: boolean
}) {
  const [name, setName] = useState('')
  const [url, setUrl] = useState('ghcr.io')
  const [credentialId, setCredentialId] = useState<string>(NONE)

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    onSubmit({
      name,
      url,
      credentialId: credentialId === NONE ? null : Number(credentialId),
    })
  }

  const fields = (
    <>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" required hint="A label to recognize this registry.">
          {({ id, describedBy }) => (
            <Input
              id={id}
              aria-describedby={describedBy}
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
              placeholder="GitHub Container Registry"
            />
          )}
        </Field>
        <Field label="URL" required hint="Registry host, e.g. ghcr.io or docker.io">
          {({ id, describedBy }) => (
            <Input
              id={id}
              aria-describedby={describedBy}
              mono
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              required
              placeholder="ghcr.io"
            />
          )}
        </Field>
      </div>
      <Field label="Credential" hint="Only needed for private registries. Leave as None to pull anonymously.">
        <Select value={credentialId} onValueChange={setCredentialId}>
          <SelectTrigger>
            <SelectValue placeholder="None" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={NONE}>None (unauthenticated)</SelectItem>
            {credentials.map((c) => (
              <SelectItem key={c.id} value={String(c.id)}>
                {c.name} ({c.username})
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </Field>
    </>
  )

  if (inDialog) {
    return (
      <form onSubmit={handleSubmit} className="flex flex-col gap-4">
        {fields}
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button type="button" variant="secondary" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" variant="primary" loading={saving}>
            <Plus /> Add registry
          </Button>
        </div>
      </form>
    )
  }

  return (
    <Card>
      <form onSubmit={handleSubmit} className="flex flex-col gap-4">
        <h2 className="text-[17px] font-semibold text-text">Add registry</h2>
        {fields}
        <div className="flex gap-2">
          <Button type="submit" variant="primary" loading={saving}>
            <Plus /> Add registry
          </Button>
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      </form>
    </Card>
  )
}
