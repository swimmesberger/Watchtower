import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Ban,
  CircleCheck,
  KeyRound,
  MoreHorizontal,
  Pencil,
  Plus,
  Trash2,
  Users as UsersIcon,
} from 'lucide-react'
import { api } from '@/lib/api'
import type { User, CreateUserRequest, UpdateUserRequest } from '@/lib/types'
import { timeAgo, absoluteTitle } from '@/lib/format'
import { toast } from '@/components/ui/use-toast'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
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

export function UsersPage() {
  const qc = useQueryClient()

  const {
    data: users = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['users'],
    queryFn: api.users.list,
  })

  const [showCreate, setShowCreate] = useState(false)
  const [editing, setEditing] = useState<User | null>(null)
  const [resetting, setResetting] = useState<User | null>(null)
  const [pendingToggle, setPendingToggle] = useState<User | null>(null)
  const [pendingDelete, setPendingDelete] = useState<User | null>(null)

  function invalidate() {
    qc.invalidateQueries({ queryKey: ['users'] })
  }

  const create = useMutation({
    mutationFn: (data: CreateUserRequest) => api.users.create(data),
    onSuccess: (_user, vars) => {
      invalidate()
      setShowCreate(false)
      toast.success(`User ${vars.userName} created.`)
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to create the user.')),
  })

  const update = useMutation({
    mutationFn: (vars: { id: number; data: UpdateUserRequest }) =>
      api.users.update(vars.id, vars.data),
    onSuccess: (_user, vars) => {
      invalidate()
      setEditing(null)
      toast.success(`Saved ${vars.data.userName}.`)
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to save the user.')),
  })

  const resetPassword = useMutation({
    mutationFn: (vars: { id: number; password: string }) =>
      api.users.resetPassword(vars.id, vars.password),
    onSuccess: () => {
      invalidate()
      toast.success(
        `Password changed for ${resetting?.userName ?? 'the account'}.`,
        'Every session it held has been signed out.',
      )
      setResetting(null)
    },
    onError: (err: Error) => toast.error(messageOf(err, 'Failed to change the password.')),
  })

  const setDisabled = useMutation({
    mutationFn: (vars: { id: number; disabled: boolean }) =>
      api.users.setDisabled(vars.id, vars.disabled),
    onSuccess: (user) => {
      invalidate()
      toast.success(user.disabled ? `${user.userName} disabled.` : `${user.userName} enabled.`)
      setPendingToggle(null)
    },
    onError: (err: Error) => {
      toast.error(messageOf(err, 'Failed to change the account status.'))
      setPendingToggle(null)
    },
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.users.delete(id),
    onSuccess: () => {
      invalidate()
      toast.success(`Deleted ${pendingDelete?.userName ?? 'user'}.`)
      setPendingDelete(null)
    },
    onError: (err: Error) => {
      toast.error(messageOf(err, 'Failed to delete the user.'))
      setPendingDelete(null)
    },
  })

  const columns: DataListColumn<User>[] = [
    {
      key: 'userName',
      header: 'User',
      cell: (u) => <span className="font-medium text-text">{u.userName}</span>,
    },
    {
      key: 'email',
      header: 'Email',
      cell: (u) => <span className="text-sm text-text-2">{u.email ?? '—'}</span>,
    },
    {
      key: 'role',
      header: 'Role',
      cell: (u) =>
        u.isAdmin ? <Badge tone="brand">Admin</Badge> : <span className="text-sm text-text-3">User</span>,
    },
    {
      key: 'status',
      header: 'Status',
      cell: (u) => <StatusBadge user={u} />,
    },
    {
      key: 'created',
      header: 'Created',
      cell: (u) => (
        <span className="tnum text-sm text-text-2" title={absoluteTitle(u.createdAt)}>
          {timeAgo(u.createdAt)}
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      className: 'w-px',
      cell: (u) => (
        <RowActions
          user={u}
          onEdit={() => setEditing(u)}
          onResetPassword={() => setResetting(u)}
          onToggleDisabled={() => setPendingToggle(u)}
          onDelete={() => setPendingDelete(u)}
        />
      ),
    },
  ]

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-6 px-4 py-6 md:px-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Users</h1>
          <p className="mt-1 text-sm text-text-2">
            Accounts that can sign in to Watchtower and reach the apps it protects.
          </p>
        </div>
        <Button variant="primary" onClick={() => setShowCreate(true)}>
          <Plus /> Add user
        </Button>
      </div>

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn't load users"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          Something went wrong while fetching the account list.
        </Banner>
      ) : (
        <DataList
          items={users}
          getKey={(u) => u.id}
          columns={columns}
          renderCard={(u) => (
            <UserCard
              user={u}
              onEdit={() => setEditing(u)}
              onResetPassword={() => setResetting(u)}
              onToggleDisabled={() => setPendingToggle(u)}
              onDelete={() => setPendingDelete(u)}
            />
          )}
          skeletonRows={isLoading ? 5 : undefined}
          emptyState={
            <EmptyState
              icon={UsersIcon}
              title="No users"
              description="Add an account so someone else can sign in."
              action={
                <Button variant="primary" onClick={() => setShowCreate(true)}>
                  <Plus /> Add user
                </Button>
              }
            />
          }
          aria-label="Users"
        />
      )}

      <Dialog open={showCreate} onOpenChange={setShowCreate}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add user</DialogTitle>
            <DialogDescription>
              The account can sign in immediately with the password you set here.
            </DialogDescription>
          </DialogHeader>
          <CreateUserForm
            saving={create.isPending}
            onCancel={() => setShowCreate(false)}
            onSubmit={(data) => create.mutate(data)}
          />
        </DialogContent>
      </Dialog>

      <Dialog open={editing != null} onOpenChange={(open) => !open && setEditing(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit {editing?.userName}</DialogTitle>
            <DialogDescription>
              Changing the password is a separate action — it also signs the account out everywhere.
            </DialogDescription>
          </DialogHeader>
          {editing && (
            <EditUserForm
              key={editing.id}
              user={editing}
              saving={update.isPending}
              onCancel={() => setEditing(null)}
              onSubmit={(data) => update.mutate({ id: editing.id, data })}
            />
          )}
        </DialogContent>
      </Dialog>

      <Dialog open={resetting != null} onOpenChange={(open) => !open && setResetting(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Set a new password</DialogTitle>
            <DialogDescription>
              {resetting
                ? `${resetting.userName} will be signed out of every session and must use the new password.`
                : ''}
            </DialogDescription>
          </DialogHeader>
          {resetting && (
            <ResetPasswordForm
              key={resetting.id}
              saving={resetPassword.isPending}
              onCancel={() => setResetting(null)}
              onSubmit={(password) => resetPassword.mutate({ id: resetting.id, password })}
            />
          )}
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={pendingToggle != null}
        onOpenChange={(open) => {
          if (!open) setPendingToggle(null)
        }}
        title={
          pendingToggle?.disabled
            ? `Enable ${pendingToggle.userName}?`
            : `Disable ${pendingToggle?.userName ?? 'user'}?`
        }
        description={
          pendingToggle?.disabled
            ? 'The account can sign in again, and any lockout from failed attempts is cleared.'
            : 'The account is kept but can no longer sign in, and its open sessions are signed out immediately.'
        }
        confirmLabel={pendingToggle?.disabled ? 'Enable' : 'Disable'}
        tone={pendingToggle?.disabled ? 'brand' : 'danger'}
        loading={setDisabled.isPending}
        onConfirm={() => {
          if (pendingToggle) {
            setDisabled.mutate({ id: pendingToggle.id, disabled: !pendingToggle.disabled })
          }
        }}
      />

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.userName}?` : 'Delete user?'}
        description="This permanently removes the account and its sessions. Disable it instead if you only want to suspend access."
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

function StatusBadge({ user }: { user: User }) {
  if (user.disabled) return <Badge tone="danger">Disabled</Badge>
  if (user.lockedOut) return <Badge tone="warn">Locked out</Badge>
  return <Badge tone="ok">Active</Badge>
}

function RowActions({
  user,
  onEdit,
  onResetPassword,
  onToggleDisabled,
  onDelete,
}: {
  user: User
  onEdit: () => void
  onResetPassword: () => void
  onToggleDisabled: () => void
  onDelete: () => void
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${user.userName}`}>
          <MoreHorizontal />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={onEdit}>
          <Pencil /> Edit
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={onResetPassword}>
          <KeyRound /> Set password
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={onToggleDisabled}>
          {user.disabled ? (
            <>
              <CircleCheck /> Enable
            </>
          ) : (
            <>
              <Ban /> Disable
            </>
          )}
        </DropdownMenuItem>
        <DropdownMenuItem destructive onSelect={onDelete}>
          <Trash2 /> Delete
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function UserCard({
  user,
  onEdit,
  onResetPassword,
  onToggleDisabled,
  onDelete,
}: {
  user: User
  onEdit: () => void
  onResetPassword: () => void
  onToggleDisabled: () => void
  onDelete: () => void
}) {
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="font-medium text-text">{user.userName}</span>
          {user.isAdmin && (
            <Badge tone="brand" size="sm">
              Admin
            </Badge>
          )}
        </div>
        <div className="mt-1 truncate text-sm text-text-2">{user.email ?? '—'}</div>
        <div className="mt-2 flex items-center gap-2 text-xs text-text-3">
          <StatusBadge user={user} />
          <span className="tnum" title={absoluteTitle(user.createdAt)}>
            Created {timeAgo(user.createdAt)}
          </span>
        </div>
      </div>
      <RowActions
        user={user}
        onEdit={onEdit}
        onResetPassword={onResetPassword}
        onToggleDisabled={onToggleDisabled}
        onDelete={onDelete}
      />
    </div>
  )
}

/** Shared admin toggle — the one field that decides whether an account can manage other accounts. */
function AdminToggle({
  checked,
  onChange,
}: {
  checked: boolean
  onChange: (value: boolean) => void
}) {
  return (
    <Field label="Administrator" hint="Can manage users and change system configuration.">
      {({ id, describedBy }) => (
        <Switch id={id} aria-describedby={describedBy} checked={checked} onCheckedChange={onChange} />
      )}
    </Field>
  )
}

function CreateUserForm({
  saving,
  onCancel,
  onSubmit,
}: {
  saving: boolean
  onCancel: () => void
  onSubmit: (data: CreateUserRequest) => void
}) {
  const [form, setForm] = useState<CreateUserRequest>({
    userName: '',
    password: '',
    email: '',
    isAdmin: false,
  })

  const canSubmit = form.userName.trim() !== '' && form.password !== ''

  return (
    <form
      className="mt-2 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (!canSubmit || saving) return
        onSubmit({ ...form, userName: form.userName.trim(), email: form.email?.trim() || null })
      }}
    >
      <Field label="User name" required>
        {({ id }) => (
          <Input
            id={id}
            value={form.userName}
            onChange={(e) => setForm((f) => ({ ...f, userName: e.target.value }))}
            placeholder="jane"
            autoComplete="off"
            autoFocus
          />
        )}
      </Field>

      <Field label="Password" required hint="At least 10 characters.">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            type="password"
            value={form.password}
            onChange={(e) => setForm((f) => ({ ...f, password: e.target.value }))}
            autoComplete="new-password"
          />
        )}
      </Field>

      <Field label="Email" hint="Optional. Forwarded to protected apps; not used to sign in.">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            type="email"
            value={form.email ?? ''}
            onChange={(e) => setForm((f) => ({ ...f, email: e.target.value }))}
            placeholder="jane@example.com"
          />
        )}
      </Field>

      <AdminToggle
        checked={form.isAdmin}
        onChange={(isAdmin) => setForm((f) => ({ ...f, isAdmin }))}
      />

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

function EditUserForm({
  user,
  saving,
  onCancel,
  onSubmit,
}: {
  user: User
  saving: boolean
  onCancel: () => void
  onSubmit: (data: UpdateUserRequest) => void
}) {
  const [form, setForm] = useState<UpdateUserRequest>({
    userName: user.userName,
    email: user.email ?? '',
    isAdmin: user.isAdmin,
  })

  const canSubmit = form.userName.trim() !== ''

  return (
    <form
      className="mt-2 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (!canSubmit || saving) return
        onSubmit({ ...form, userName: form.userName.trim(), email: form.email?.trim() || null })
      }}
    >
      <Field label="User name" required>
        {({ id }) => (
          <Input
            id={id}
            value={form.userName}
            onChange={(e) => setForm((f) => ({ ...f, userName: e.target.value }))}
            autoComplete="off"
            autoFocus
          />
        )}
      </Field>

      <Field label="Email" hint="Optional. Forwarded to protected apps; not used to sign in.">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            type="email"
            value={form.email ?? ''}
            onChange={(e) => setForm((f) => ({ ...f, email: e.target.value }))}
            placeholder="jane@example.com"
          />
        )}
      </Field>

      <AdminToggle
        checked={form.isAdmin}
        onChange={(isAdmin) => setForm((f) => ({ ...f, isAdmin }))}
      />

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

function ResetPasswordForm({
  saving,
  onCancel,
  onSubmit,
}: {
  saving: boolean
  onCancel: () => void
  onSubmit: (password: string) => void
}) {
  const [password, setPassword] = useState('')

  return (
    <form
      className="mt-2 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (password === '' || saving) return
        onSubmit(password)
      }}
    >
      <Field label="New password" required hint="At least 10 characters.">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="new-password"
            autoFocus
          />
        )}
      </Field>

      <div className="flex justify-end gap-2 pt-1">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" loading={saving} disabled={password === ''}>
          Set password
        </Button>
      </div>
    </form>
  )
}
