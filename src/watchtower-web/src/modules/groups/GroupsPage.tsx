import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { MoreHorizontal, Pencil, Plus, Trash2, UserCog, UsersRound } from 'lucide-react'
import { api } from '@/lib/api'
import type { Group, Realm } from '@/lib/types'
import { ALL_REALMS, useRealms } from '@/hooks/use-realms'
import { toast } from '@/components/ui/use-toast'
import { Button } from '@/components/ui/button'
import { Banner } from '@/components/ui/banner'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
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

/** Plural-aware member count for the list and the cards. */
function describeMembers(count: number): string {
  return count === 1 ? '1 member' : `${count} members`
}

export function GroupsPage() {
  const qc = useQueryClient()
  const { realms, nameOf, systemRealmId } = useRealms()

  // Same shape as the Users screen: "All realms" by default, filtered client-side off one cached roster.
  const [realmFilter, setRealmFilter] = useState<string>(ALL_REALMS)

  const {
    data: groups = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['groups'],
    queryFn: () => api.groups.list(),
  })

  const visibleGroups =
    realmFilter === ALL_REALMS
      ? groups
      : groups.filter((g) => g.realmId === Number(realmFilter))

  const [showCreate, setShowCreate] = useState(false)
  const [renaming, setRenaming] = useState<Group | null>(null)
  const [editingMembers, setEditingMembers] = useState<Group | null>(null)
  const [pendingDelete, setPendingDelete] = useState<Group | null>(null)

  function invalidate() {
    qc.invalidateQueries({ queryKey: ['groups'] })
    // The realm roster carries a groupCount, and the Realms screen's delete guard reads it.
    qc.invalidateQueries({ queryKey: ['realms'] })
  }

  const create = useMutation({
    mutationFn: (vars: { name: string; realmId: number }) =>
      api.groups.create(vars.name, vars.realmId),
    onSuccess: (group) => {
      invalidate()
      setShowCreate(false)
      toast.success(`Group ${group.name} created.`)
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to create the group.')),
  })

  const rename = useMutation({
    mutationFn: (vars: { id: number; name: string }) => api.groups.rename(vars.id, vars.name),
    onSuccess: (group) => {
      invalidate()
      setRenaming(null)
      toast.success(
        `Renamed to ${group.name}.`,
        'Protected apps will see the new name from the next request.',
      )
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to rename the group.')),
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.groups.delete(id),
    onSuccess: () => {
      invalidate()
      toast.success(`Deleted ${pendingDelete?.name ?? 'group'}.`)
      setPendingDelete(null)
    },
    onError: (err: Error) => {
      toast.error(messageOf(err, 'Failed to delete the group.'))
      setPendingDelete(null)
    },
  })

  const columns: DataListColumn<Group>[] = [
    {
      key: 'name',
      header: 'Group',
      cell: (g) => <span className="font-medium text-text">{g.name}</span>,
    },
    {
      key: 'realm',
      header: 'Realm',
      cell: (g) => <span className="text-sm text-text-2">{nameOf(g.realmId)}</span>,
    },
    {
      key: 'members',
      header: 'Members',
      cell: (g) => <span className="tnum text-sm text-text-2">{describeMembers(g.memberCount)}</span>,
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      className: 'w-px',
      cell: (g) => (
        <RowActions
          group={g}
          onEditMembers={() => setEditingMembers(g)}
          onRename={() => setRenaming(g)}
          onDelete={() => setPendingDelete(g)}
        />
      ),
    },
  ]

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-6 px-4 py-6 md:px-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Groups</h1>
          <p className="mt-1 text-sm text-text-2">
            Named sets of accounts. Grant a route to a group and every member gets in — and the group
            name is forwarded to the app, so it can map it onto its own roles.
          </p>
        </div>
        <Button variant="primary" onClick={() => setShowCreate(true)}>
          <Plus /> Add group
        </Button>
      </div>

      {/* Only worth showing once there is more than one population to choose between. */}
      {realms.length > 1 && (
        <div className="flex flex-wrap items-end gap-3">
          <Field label="Realm" className="w-full sm:w-64">
            {({ id }) => (
              <Select value={realmFilter} onValueChange={setRealmFilter}>
                <SelectTrigger id={id}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL_REALMS}>All realms</SelectItem>
                  {realms.map((r) => (
                    <SelectItem key={r.id} value={String(r.id)}>
                      {r.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </Field>
        </div>
      )}

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn't load groups"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          Something went wrong while fetching the group list.
        </Banner>
      ) : (
        <DataList
          items={visibleGroups}
          getKey={(g) => g.id}
          columns={columns}
          renderCard={(g) => (
            <GroupCard
              group={g}
              realmName={nameOf(g.realmId)}
              onEditMembers={() => setEditingMembers(g)}
              onRename={() => setRenaming(g)}
              onDelete={() => setPendingDelete(g)}
            />
          )}
          skeletonRows={isLoading ? 5 : undefined}
          emptyState={
            <EmptyState
              icon={UsersRound}
              title="No groups"
              description="Create a group to grant several accounts the same routes at once."
              action={
                <Button variant="primary" onClick={() => setShowCreate(true)}>
                  <Plus /> Add group
                </Button>
              }
            />
          }
          aria-label="Groups"
        />
      )}

      <Dialog open={showCreate} onOpenChange={setShowCreate}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add group</DialogTitle>
            <DialogDescription>
              The group starts empty. Add members once it exists, then grant it routes from the Routes
              page.
            </DialogDescription>
          </DialogHeader>
          <NameForm
            submitLabel="Create"
            realms={realms}
            defaultRealmId={
              // Pre-select whatever the roster is filtered to, like the Users screen does.
              realmFilter === ALL_REALMS ? systemRealmId : Number(realmFilter)
            }
            saving={create.isPending}
            onCancel={() => setShowCreate(false)}
            onSubmit={(name, realmId) =>
              create.mutate({ name, realmId: realmId ?? systemRealmId })
            }
          />
        </DialogContent>
      </Dialog>

      <Dialog open={renaming != null} onOpenChange={(open) => !open && setRenaming(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Rename {renaming?.name}</DialogTitle>
            <DialogDescription>
              Members and route grants follow the group, not its name. But protected apps receive the
              name — one that maps group names to roles will stop recognising the old one.
            </DialogDescription>
          </DialogHeader>
          {renaming && (
            <NameForm
              key={renaming.id}
              initial={renaming.name}
              submitLabel="Save"
              saving={rename.isPending}
              onCancel={() => setRenaming(null)}
              onSubmit={(name) => rename.mutate({ id: renaming.id, name })}
            />
          )}
        </DialogContent>
      </Dialog>

      <MembersDialog group={editingMembers} onClose={() => setEditingMembers(null)} />

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.name}?` : 'Delete group?'}
        description="This removes the group, its memberships, and every route grant that names it. Members who reached a route only through this group lose access immediately. The accounts themselves are untouched."
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

function RowActions({
  group,
  onEditMembers,
  onRename,
  onDelete,
}: {
  group: Group
  onEditMembers: () => void
  onRename: () => void
  onDelete: () => void
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${group.name}`}>
          <MoreHorizontal />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={onEditMembers}>
          <UserCog /> Members
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={onRename}>
          <Pencil /> Rename
        </DropdownMenuItem>
        <DropdownMenuItem destructive onSelect={onDelete}>
          <Trash2 /> Delete
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function GroupCard({
  group,
  realmName,
  onEditMembers,
  onRename,
  onDelete,
}: {
  group: Group
  realmName: string
  onEditMembers: () => void
  onRename: () => void
  onDelete: () => void
}) {
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0 flex-1">
        <div className="font-medium text-text">{group.name}</div>
        <div className="mt-1 text-sm text-text-2">
          {realmName} · {describeMembers(group.memberCount)}
        </div>
      </div>
      <RowActions
        group={group}
        onEditMembers={onEditMembers}
        onRename={onRename}
        onDelete={onDelete}
      />
    </div>
  )
}

/**
 * Create and rename share one form: both submit a single name against the same server-side rules, so a
 * second copy would only be a second place for the copy to drift. Create additionally picks a realm —
 * rename passes no `realms`, and the field is simply absent, because a group never changes population.
 */
function NameForm({
  initial = '',
  submitLabel,
  realms,
  defaultRealmId,
  saving,
  onCancel,
  onSubmit,
}: {
  initial?: string
  submitLabel: string
  realms?: Realm[]
  defaultRealmId?: number
  saving: boolean
  onCancel: () => void
  onSubmit: (name: string, realmId?: number) => void
}) {
  const [name, setName] = useState(initial)
  const [realmId, setRealmId] = useState<number | undefined>(defaultRealmId)
  const canSubmit = name.trim() !== ''

  return (
    <form
      className="mt-2 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (!canSubmit || saving) return
        onSubmit(name.trim(), realmId)
      }}
    >
      <Field
        label="Name"
        required
        hint="Letters, digits and plain punctuation, up to 64 characters. No commas — apps receive group names as a comma-separated list."
      >
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="platform"
            autoComplete="off"
            autoFocus
          />
        )}
      </Field>

      {realms && realms.length > 1 && (
        <Field
          label="Realm"
          hint="The population the group draws its members from. Fixed once created — a group only ever holds accounts from its own realm."
        >
          {({ id, describedBy }) => (
            <Select
              value={realmId != null ? String(realmId) : undefined}
              onValueChange={(v) => setRealmId(Number(v))}
            >
              <SelectTrigger id={id} aria-describedby={describedBy}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {realms.map((r) => (
                  <SelectItem key={r.id} value={String(r.id)}>
                    {r.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        </Field>
      )}

      <div className="flex justify-end gap-2 pt-1">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" loading={saving} disabled={!canSubmit}>
          {submitLabel}
        </Button>
      </div>
    </form>
  )
}

/**
 * Loads the account roster and the group's current membership, then saves the whole set at once — the
 * same shape the server takes, so what the checkboxes show is exactly what gets written.
 */
function MembersDialog({ group, onClose }: { group: Group | null; onClose: () => void }) {
  const qc = useQueryClient()
  const open = group != null

  // Scoped to the group's realm, because a group may only ever hold accounts of its own population —
  // groups.setMembers refuses anything else, so a candidate from another realm is a checkbox that can
  // only produce a rejected save.
  const { data: users = [], isLoading: usersLoading } = useQuery({
    queryKey: ['users', { realmId: group?.realmId }],
    queryFn: () => api.users.list(group!.realmId),
    enabled: open,
  })

  const {
    data: memberIds,
    isLoading: membersLoading,
    isError,
  } = useQuery({
    queryKey: ['group-members', group?.id],
    queryFn: () => api.groups.getMembers(group!.id),
    enabled: open,
  })

  const save = useMutation({
    mutationFn: (userIds: number[]) => api.groups.setMembers(group!.id, userIds),
    onSuccess: (userIds) => {
      // Both: the roster's member counts change, and this group's membership is now what we just sent.
      qc.invalidateQueries({ queryKey: ['groups'] })
      qc.setQueryData(['group-members', group!.id], userIds)
      toast.success(
        `Members updated for ${group!.name}.`,
        'Route access follows immediately — there is nothing to reload.',
      )
      onClose()
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to update the members.')),
  })

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !save.isPending && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Members · {group?.name}</DialogTitle>
          <DialogDescription>
            Everyone ticked here reaches every route this group is granted. Changes take effect on the
            next request. Only accounts in the group’s own realm can be members.
          </DialogDescription>
        </DialogHeader>

        {isError ? (
          <Banner tone="danger" title="Couldn’t load the members">
            Something went wrong while fetching this group’s membership.
          </Banner>
        ) : usersLoading || membersLoading || memberIds === undefined ? (
          <div className="flex flex-col gap-3 py-2">
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-20 w-full" />
          </div>
        ) : (
          <MembersForm
            key={group!.id}
            users={users}
            initial={memberIds}
            saving={save.isPending}
            onCancel={onClose}
            onSubmit={(userIds) => save.mutate(userIds)}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

function MembersForm({
  users,
  initial,
  saving,
  onCancel,
  onSubmit,
}: {
  users: { id: number; userName: string; email: string | null }[]
  initial: number[]
  saving: boolean
  onCancel: () => void
  onSubmit: (userIds: number[]) => void
}) {
  const [memberIds, setMemberIds] = useState<number[]>(initial)

  function toggle(id: number) {
    setMemberIds((ids) => (ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id]))
  }

  return (
    <form
      className="mt-1 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (saving) return
        onSubmit(memberIds)
      }}
    >
      <Field label="Accounts">
        {() =>
          users.length === 0 ? (
            <p className="text-[13px] text-text-3">
              No accounts in this realm yet. Add them on the Users page, then put them in this group.
            </p>
          ) : (
            <div className="max-h-52 overflow-y-auto rounded-md border border-border">
              {users.map((u) => (
                <label
                  key={u.id}
                  className="flex cursor-pointer items-center gap-3 border-b border-border px-3 py-2 last:border-b-0 hover:bg-surface-2"
                >
                  <input
                    type="checkbox"
                    className="size-4 accent-brand"
                    checked={memberIds.includes(u.id)}
                    onChange={() => toggle(u.id)}
                  />
                  <span className="min-w-0 flex-1">
                    <span className="text-sm text-text">{u.userName}</span>
                    {u.email && <span className="ml-2 text-xs text-text-3">{u.email}</span>}
                  </span>
                </label>
              ))}
            </div>
          )
        }
      </Field>

      <div className="flex justify-end gap-2 pt-1">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" loading={saving} disabled={users.length === 0}>
          Save
        </Button>
      </div>
    </form>
  )
}
