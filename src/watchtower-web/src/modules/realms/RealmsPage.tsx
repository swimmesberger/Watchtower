import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Building2, MoreHorizontal, Pencil, Plus, Trash2 } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateRealmRequest, Realm, UpdateRealmRequest } from '@/lib/types'
import { timeAgo, absoluteTitle } from '@/lib/format'
import { toast } from '@/components/ui/use-toast'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog'

/** The RPC layer throws `RpcError`, whose message is the backend's `AppError` text — show that. */
function messageOf(error: Error, fallback: string): string {
  return error.message || fallback
}

/** What a realm is holding. The server refuses to delete a realm that holds anything, so we say so first. */
function referenceCount(realm: Realm): number {
  return realm.userCount + realm.groupCount + realm.templateCount
}

/**
 * Why the realm cannot be deleted, or null when it can. Mirrors the two refusals `realms.delete` makes;
 * the server still decides, this only keeps an administrator from clicking into a `Conflict`. Kept to two
 * words because it is shown beside the disabled menu item — the row already says what the realm holds.
 */
function deleteBlockedReason(realm: Realm): string | null {
  if (realm.isSystem) return 'built in'
  if (referenceCount(realm) > 0) return 'not empty'
  return null
}

export function RealmsPage() {
  const qc = useQueryClient()

  const {
    data: realms = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['realms'],
    queryFn: api.realms.list,
  })

  const [showCreate, setShowCreate] = useState(false)
  const [editing, setEditing] = useState<Realm | null>(null)
  const [pendingDelete, setPendingDelete] = useState<Realm | null>(null)

  function invalidate() {
    qc.invalidateQueries({ queryKey: ['realms'] })
  }

  const create = useMutation({
    mutationFn: (data: CreateRealmRequest) => api.realms.create(data),
    onSuccess: (realm) => {
      invalidate()
      setShowCreate(false)
      toast.success(
        `Realm ${realm.name} created.`,
        realm.authHost
          ? `Its login page is served on ${realm.authHost}.`
          : 'Give it a login host once DNS points at Watchtower — until then nobody can sign in to it.',
      )
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to create the realm.')),
  })

  const update = useMutation({
    mutationFn: (vars: { id: number; data: UpdateRealmRequest }) =>
      api.realms.update(vars.id, vars.data),
    onSuccess: (realm) => {
      invalidate()
      setEditing(null)
      toast.success(`Saved ${realm.name}.`)
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to save the realm.')),
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.realms.remove(id),
    onSuccess: () => {
      invalidate()
      toast.success(`Deleted ${pendingDelete?.name ?? 'realm'}.`)
      setPendingDelete(null)
    },
    onError: (err: Error) => {
      toast.error(messageOf(err, 'Failed to delete the realm.'))
      setPendingDelete(null)
    },
  })

  const columns: DataListColumn<Realm>[] = [
    {
      key: 'name',
      header: 'Realm',
      cell: (r) => (
        <span className="flex items-center gap-2">
          <span className="font-medium text-text">{r.name}</span>
          {r.isSystem && (
            <Badge tone="brand" size="sm">
              System
            </Badge>
          )}
        </span>
      ),
    },
    {
      key: 'slug',
      header: 'Slug',
      cell: (r) => <span className="font-mono text-[13px] text-text-2">{r.slug}</span>,
    },
    {
      key: 'authHost',
      header: 'Login host',
      cell: (r) => <AuthHostCell realm={r} />,
    },
    {
      key: 'contents',
      header: 'Holds',
      cell: (r) => <span className="tnum text-sm text-text-2">{describeContents(r)}</span>,
    },
    {
      key: 'created',
      header: 'Created',
      cell: (r) => (
        <span className="tnum text-sm text-text-2" title={absoluteTitle(r.createdAt)}>
          {timeAgo(r.createdAt)}
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      className: 'w-px',
      cell: (r) => (
        <RowActions realm={r} onEdit={() => setEditing(r)} onDelete={() => setPendingDelete(r)} />
      ),
    },
  ]

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-6 px-4 py-6 md:px-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Realms</h1>
          <p className="mt-1 text-sm text-text-2">
            Separate user populations, each with its own accounts and its own login host. The operator
            realm is the one this management UI belongs to; every other realm reaches only the apps whose
            template is placed in it.
          </p>
        </div>
        <Button variant="primary" onClick={() => setShowCreate(true)}>
          <Plus /> Add realm
        </Button>
      </div>

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn't load realms"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          Something went wrong while fetching the realm list.
        </Banner>
      ) : (
        <DataList
          items={realms}
          getKey={(r) => r.id}
          columns={columns}
          renderCard={(r) => (
            <RealmCard
              realm={r}
              onEdit={() => setEditing(r)}
              onDelete={() => setPendingDelete(r)}
            />
          )}
          skeletonRows={isLoading ? 3 : undefined}
          emptyState={
            <EmptyState
              icon={Building2}
              title="No realms"
              description="Add a realm to give a set of applications its own accounts and login page."
              action={
                <Button variant="primary" onClick={() => setShowCreate(true)}>
                  <Plus /> Add realm
                </Button>
              }
            />
          }
          aria-label="Realms"
        />
      )}

      <Dialog open={showCreate} onOpenChange={setShowCreate}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add realm</DialogTitle>
            <DialogDescription>
              The realm starts empty — it holds nobody and reaches nothing until accounts are created in
              it and a template is placed in it.
            </DialogDescription>
          </DialogHeader>
          <CreateRealmForm
            saving={create.isPending}
            onCancel={() => setShowCreate(false)}
            onSubmit={(data) => create.mutate(data)}
          />
        </DialogContent>
      </Dialog>

      <Dialog open={editing != null} onOpenChange={(open) => !open && setEditing(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit {editing?.name}</DialogTitle>
            <DialogDescription>
              The slug is fixed: it travels in the `realm` claim every app in this realm receives, so
              changing it would change what they are told about their users.
            </DialogDescription>
          </DialogHeader>
          {editing && (
            <EditRealmForm
              key={editing.id}
              realm={editing}
              saving={update.isPending}
              onCancel={() => setEditing(null)}
              onSubmit={(data) => update.mutate({ id: editing.id, data })}
            />
          )}
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.name}?` : 'Delete realm?'}
        description="The realm is removed along with its login host. Sessions on that host stop working immediately."
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

/** Plural-aware summary of what a realm is holding — the same three counts the delete guard reads. */
function describeContents(realm: Realm): string {
  const parts: string[] = []
  if (realm.userCount > 0) parts.push(`${realm.userCount} user${realm.userCount === 1 ? '' : 's'}`)
  if (realm.groupCount > 0) parts.push(`${realm.groupCount} group${realm.groupCount === 1 ? '' : 's'}`)
  if (realm.templateCount > 0)
    parts.push(`${realm.templateCount} template${realm.templateCount === 1 ? '' : 's'}`)
  return parts.length === 0 ? 'empty' : parts.join(' · ')
}

function AuthHostCell({ realm }: { realm: Realm }) {
  if (realm.isSystem) {
    return <span className="text-sm text-text-3">Watchtower’s own host</span>
  }
  return realm.authHost ? (
    <span className="font-mono text-[13px] text-text-2">{realm.authHost}</span>
  ) : (
    <Badge tone="warn" size="sm">
      No login host
    </Badge>
  )
}

function RowActions({
  realm,
  onEdit,
  onDelete,
}: {
  realm: Realm
  onEdit: () => void
  onDelete: () => void
}) {
  const blocked = deleteBlockedReason(realm)

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${realm.name}`}>
          <MoreHorizontal />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={onEdit}>
          <Pencil /> Edit
        </DropdownMenuItem>
        {blocked ? (
          // A disabled menu item takes no pointer events and no focus, so a tooltip on it would be
          // unreachable — the reason is rendered in the item instead, where it is readable either way.
          <DropdownMenuItem disabled textValue="Delete" className="justify-between">
            <span className="flex items-center gap-2">
              <Trash2 /> Delete
            </span>
            <span className="ml-3 text-xs text-text-3">{blocked}</span>
          </DropdownMenuItem>
        ) : (
          <DropdownMenuItem destructive onSelect={onDelete}>
            <Trash2 /> Delete
          </DropdownMenuItem>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function RealmCard({
  realm,
  onEdit,
  onDelete,
}: {
  realm: Realm
  onEdit: () => void
  onDelete: () => void
}) {
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="font-medium text-text">{realm.name}</span>
          {realm.isSystem && (
            <Badge tone="brand" size="sm">
              System
            </Badge>
          )}
        </div>
        <div className="mt-1 truncate font-mono text-[13px] text-text-2">{realm.slug}</div>
        <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-text-3">
          <AuthHostCell realm={realm} />
          <span className="tnum">{describeContents(realm)}</span>
        </div>
      </div>
      <RowActions realm={realm} onEdit={onEdit} onDelete={onDelete} />
    </div>
  )
}

/** Client-side hints only — the same rules are enforced by the server, which is what actually refuses. */
const SLUG_HINT =
  'Lowercase letters, digits and single hyphens. Fixed once created — it is the `realm` claim your apps receive.'
const AUTH_HOST_HINT =
  'The bare hostname this realm’s login page is served on, e.g. login.acme.example. No scheme, port or path. Leave empty until DNS points at Watchtower.'

function CreateRealmForm({
  saving,
  onCancel,
  onSubmit,
}: {
  saving: boolean
  onCancel: () => void
  onSubmit: (data: CreateRealmRequest) => void
}) {
  const [form, setForm] = useState<CreateRealmRequest>({ name: '', slug: '', authHost: '' })
  // The slug follows the name until it is edited on its own — the common case is that they agree, and a
  // slug is immutable, so it is worth making the derived one easy to see before it is committed to.
  const [slugTouched, setSlugTouched] = useState(false)

  const slug = slugTouched ? (form.slug ?? '') : slugify(form.name)
  const canSubmit = form.name.trim() !== '' && slug.trim() !== ''

  return (
    <form
      className="mt-2 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (!canSubmit || saving) return
        onSubmit({
          name: form.name.trim(),
          slug: slug.trim(),
          authHost: form.authHost?.trim() || null,
        })
      }}
    >
      <Field label="Name" required hint="Shown here and on the realm's login page.">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            value={form.name}
            onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            placeholder="Acme"
            autoComplete="off"
            autoFocus
          />
        )}
      </Field>

      <Field label="Slug" required hint={SLUG_HINT}>
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            mono
            value={slug}
            onChange={(e) => {
              setSlugTouched(true)
              setForm((f) => ({ ...f, slug: e.target.value }))
            }}
            placeholder="acme"
            autoComplete="off"
            spellCheck={false}
          />
        )}
      </Field>

      <Field label="Login host" hint={AUTH_HOST_HINT}>
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            mono
            value={form.authHost ?? ''}
            onChange={(e) => setForm((f) => ({ ...f, authHost: e.target.value }))}
            placeholder="login.acme.example"
            autoComplete="off"
            spellCheck={false}
          />
        )}
      </Field>

      <div className="flex justify-end gap-2 pt-1">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" loading={saving} disabled={!canSubmit}>
          Create
        </Button>
      </div>
    </form>
  )
}

function EditRealmForm({
  realm,
  saving,
  onCancel,
  onSubmit,
}: {
  realm: Realm
  saving: boolean
  onCancel: () => void
  onSubmit: (data: UpdateRealmRequest) => void
}) {
  const [name, setName] = useState(realm.name)
  const [authHost, setAuthHost] = useState(realm.authHost ?? '')

  const canSubmit = name.trim() !== ''

  return (
    <form
      className="mt-2 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (!canSubmit || saving) return
        onSubmit({
          name: name.trim(),
          // Omitted for the system realm, whose login host is the configured Watchtower:Auth:Host and
          // which the server refuses to set here. Everywhere else the empty string is what clears it —
          // omitting would mean "leave alone", which is a different thing.
          authHost: realm.isSystem ? undefined : authHost.trim(),
        })
      }}
    >
      <Field label="Name" required>
        {({ id }) => (
          <Input
            id={id}
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoComplete="off"
            autoFocus
          />
        )}
      </Field>

      <Field label="Slug" hint="Fixed once created.">
        {({ id }) => <Input id={id} mono value={realm.slug} readOnly disabled />}
      </Field>

      <Field
        label="Login host"
        hint={
          realm.isSystem
            ? 'The operator realm signs in on Watchtower’s own host, from the configured Watchtower:Auth:Host — it cannot be set here.'
            : `${AUTH_HOST_HINT} Moving it signs out every session on the old host.`
        }
      >
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            mono
            value={realm.isSystem ? '' : authHost}
            onChange={(e) => setAuthHost(e.target.value)}
            placeholder={realm.isSystem ? 'Watchtower’s own host' : 'login.acme.example'}
            disabled={realm.isSystem}
            autoComplete="off"
            spellCheck={false}
          />
        )}
      </Field>

      <div className="flex justify-end gap-2 pt-1">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" loading={saving} disabled={!canSubmit}>
          Save
        </Button>
      </div>
    </form>
  )
}

/** Best-effort suggestion for the slug field; the server's rule is what decides, and it is stricter. */
function slugify(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}
