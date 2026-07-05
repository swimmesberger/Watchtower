import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { KeyRound, Plus, MoreHorizontal, Trash2 } from 'lucide-react'
import { api } from '@/lib/api'
import type { Credential, CreateCredentialRequest } from '@/lib/types'
import { useMediaQuery } from '@/hooks/use-media-query'
import { timeAgo, absoluteTitle } from '@/lib/format'
import { toast } from '@/components/ui/use-toast'
import { Button } from '@/components/ui/button'
import { Banner } from '@/components/ui/banner'
import { Card, CardContent } from '@/components/ui/card'
import { SectionHeader } from '@/components/ui/section-header'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { SecretField } from '@/components/ui/secret-field'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Tooltip } from '@/components/ui/tooltip'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog'

export function CredentialsPage() {
  const qc = useQueryClient()
  const isDesktop = useMediaQuery('(min-width: 768px)')

  const {
    data: credentials = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const [showForm, setShowForm] = useState(false)
  const [pendingDelete, setPendingDelete] = useState<Credential | null>(null)

  const create = useMutation({
    mutationFn: (data: CreateCredentialRequest) => api.credentials.create(data),
    onSuccess: (_result, vars) => {
      qc.invalidateQueries({ queryKey: ['credentials'] })
      setShowForm(false)
      toast.success(`Credential ${vars.name} added.`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to add credential.')
    },
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.credentials.delete(id),
    onSuccess: (_result, _id) => {
      qc.invalidateQueries({ queryKey: ['credentials'] })
      toast.success(`Deleted ${pendingDelete?.name ?? 'credential'}.`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to delete credential.')
    },
    onSettled: () => setPendingDelete(null),
  })

  const columns: DataListColumn<Credential>[] = [
    {
      key: 'name',
      header: 'Name',
      cell: (c) => <span className="font-medium text-text">{c.name}</span>,
    },
    {
      key: 'username',
      header: 'Username',
      cell: (c) => <span className="font-mono text-sm text-text-2">{c.username}</span>,
    },
    {
      key: 'created',
      header: 'Created',
      cell: (c) => (
        <span className="tnum text-sm text-text-2" title={absoluteTitle(c.createdAt)}>
          {timeAgo(c.createdAt)}
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      className: 'w-px',
      cell: (c) => (
        <Tooltip label="Delete">
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`Delete ${c.name}`}
            onClick={() => setPendingDelete(c)}
          >
            <Trash2 className="text-danger" />
          </Button>
        </Tooltip>
      ),
    },
  ]

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-6 px-4 py-6 md:px-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Credentials</h1>
          <p className="mt-1 text-sm text-text-2">
            Authentication tokens for private repositories and registries.
          </p>
        </div>
        <Button variant="primary" onClick={() => setShowForm(true)}>
          <Plus /> Add credential
        </Button>
      </div>

      <Banner tone="info" title="ghcr.io requires a Classic PAT">
        Fine-grained PATs can clone private repositories but <strong>cannot</strong> authenticate to
        GitHub Container Registry (ghcr.io). Use a Classic PAT with{' '}
        <code className="rounded bg-surface-2 px-1.5 py-0.5 font-mono text-[11px] text-text">
          read:packages
        </code>{' '}
        scope when linking a credential to a Registry entry.
      </Banner>

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn't load credentials"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          Something went wrong while fetching your credentials.
        </Banner>
      ) : (
        <>
          {/* Desktop: inline Card form. Mobile: Dialog (below). */}
          {isDesktop && showForm && (
            <CredentialForm
              variant="card"
              onSubmit={(data) => create.mutate(data)}
              saving={create.isPending}
              onCancel={() => setShowForm(false)}
            />
          )}

          <DataList
            items={credentials}
            getKey={(c) => c.id}
            columns={columns}
            renderCard={(c) => (
              <CredentialCard credential={c} onDelete={() => setPendingDelete(c)} />
            )}
            skeletonRows={isLoading ? 5 : undefined}
            emptyState={
              <EmptyState
                icon={KeyRound}
                title="No credentials"
                description="Store a username + token to access private repos and registries."
                action={
                  <Button variant="primary" onClick={() => setShowForm(true)}>
                    <Plus /> Add credential
                  </Button>
                }
              />
            }
            aria-label="Credentials"
          />
        </>
      )}

      {/* Mobile: add-credential Dialog */}
      {!isDesktop && (
        <Dialog open={showForm} onOpenChange={setShowForm}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Add credential</DialogTitle>
              <DialogDescription>
                Store a username + token to access private repos and registries.
              </DialogDescription>
            </DialogHeader>
            <CredentialForm
              variant="dialog"
              onSubmit={(data) => create.mutate(data)}
              saving={create.isPending}
              onCancel={() => setShowForm(false)}
            />
          </DialogContent>
        </Dialog>
      )}

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.name}?` : 'Delete credential?'}
        description="This permanently removes the credential. Stacks or registries using it will need a new credential to authenticate."
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

function CredentialCard({
  credential,
  onDelete,
}: {
  credential: Credential
  onDelete: () => void
}) {
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0 flex-1">
        <div className="font-medium text-text">{credential.name}</div>
        <div className="mt-1 truncate font-mono text-sm text-text-2">{credential.username}</div>
        <div className="mt-2 text-xs text-text-3">
          Created{' '}
          <span className="tnum" title={absoluteTitle(credential.createdAt)}>
            {timeAgo(credential.createdAt)}
          </span>
        </div>
      </div>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${credential.name}`}>
            <MoreHorizontal />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem destructive onSelect={onDelete}>
            <Trash2 /> Delete
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  )
}

function CredentialForm({
  variant,
  onSubmit,
  saving,
  onCancel,
}: {
  variant: 'card' | 'dialog'
  onSubmit: (data: CreateCredentialRequest) => void
  saving: boolean
  onCancel: () => void
}) {
  const [form, setForm] = useState<CreateCredentialRequest>({
    name: '',
    username: '',
    token: '',
  })

  const canSubmit =
    form.name.trim() !== '' && form.username.trim() !== '' && form.token.trim() !== ''

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!canSubmit || saving) return
    onSubmit(form)
  }

  const body = (
    <div className="flex flex-col gap-4">
      <Field label="Name" required>
        {({ id }) => (
          <Input
            id={id}
            value={form.name}
            onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            placeholder="GitHub (repo clone)"
            autoFocus
          />
        )}
      </Field>

      <Field
        label="Username"
        required
        hint="Your registry / VCS username, e.g. GitHub handle"
      >
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            value={form.username}
            onChange={(e) => setForm((f) => ({ ...f, username: e.target.value }))}
            placeholder="your-github-username"
          />
        )}
      </Field>

      <Field label="Token" required hint="e.g. a GitHub PAT: ghp_… or github_pat_…">
        <SecretField
          value={form.token}
          onChange={(value) => setForm((f) => ({ ...f, token: value }))}
          placeholder="ghp_… or github_pat_…"
          aria-label="Token"
        />
      </Field>

      <div className="flex justify-end gap-2 pt-1">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" loading={saving} disabled={!canSubmit}>
          Add
        </Button>
      </div>
    </div>
  )

  if (variant === 'dialog') {
    return (
      <form onSubmit={submit} className="mt-2">
        {body}
      </form>
    )
  }

  return (
    <Card>
      <CardContent>
        <SectionHeader
          title="Add credential"
          description="Only needed for private repos or registries. The token is stored securely and never shown again."
        />
        <form onSubmit={submit}>{body}</form>
      </CardContent>
    </Card>
  )
}
