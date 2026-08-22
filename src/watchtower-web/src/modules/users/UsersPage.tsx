import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Ban,
  CircleCheck,
  KeyRound,
  MoreHorizontal,
  Pencil,
  Plus,
  ShieldOff,
  Trash2,
  Users as UsersIcon,
} from 'lucide-react'
import { api } from '@/lib/api'
import type { Realm, User, CreateUserRequest, UpdateUserRequest } from '@/lib/types'
import { timeAgo, absoluteTitle } from '@/lib/format'
import { ALL_REALMS, useRealms } from '@/hooks/use-realms'
import { toast } from '@/components/ui/use-toast'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
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

export function UsersPage() {
  const qc = useQueryClient()
  const { realms, nameOf, systemRealmId, isSystem } = useRealms()

  // "All realms" by default: the management UI is operator-only and sees every population, so the filter
  // narrows the roster rather than scoping the screen. Filtering client-side keeps one cached ['users']
  // roster — the same one the Groups members dialog and the route Access dialog read.
  const [realmFilter, setRealmFilter] = useState<string>(ALL_REALMS)

  const {
    data: users = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['users'],
    queryFn: () => api.users.list(),
  })

  const visibleUsers =
    realmFilter === ALL_REALMS
      ? users
      : users.filter((u) => u.realmId === Number(realmFilter))

  // One rule for every realm-aware control on this screen: the realm column, the filter and the create
  // dialog's realm select all appear together, and only when there is a choice to be made.
  const showRealm = realms.length > 1

  const [showCreate, setShowCreate] = useState(false)
  const [editing, setEditing] = useState<User | null>(null)
  const [resetting, setResetting] = useState<User | null>(null)
  const [pendingToggle, setPendingToggle] = useState<User | null>(null)
  const [pendingDelete, setPendingDelete] = useState<User | null>(null)
  const [pendingMfaReset, setPendingMfaReset] = useState<User | null>(null)

  function invalidate() {
    qc.invalidateQueries({ queryKey: ['users'] })
    // The realm roster carries a userCount, and it is what the Realms screen's delete guard reads —
    // creating or removing an account changes it.
    qc.invalidateQueries({ queryKey: ['realms'] })
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

  // One-directional: this can take a second factor away and there is no call that adds one, because
  // enrolling needs a code only the account's owner can produce. It exists for the mishap recovery codes
  // do not cover — a phone lost along with the printed list.
  const resetMfa = useMutation({
    mutationFn: (id: number) => api.users.resetMfa(id),
    onSuccess: (wasEnabled) => {
      invalidate()
      toast.success(
        wasEnabled
          ? `Two-factor authentication cleared for ${pendingMfaReset?.userName ?? 'the account'}.`
          : `${pendingMfaReset?.userName ?? 'The account'} had no two-factor enrolment.`,
        wasEnabled ? 'They can sign in with their password alone and enrol again.' : undefined,
      )
      setPendingMfaReset(null)
    },
    onError: (err: Error) => {
      toast.error(messageOf(err, 'Failed to reset two-factor authentication.'))
      setPendingMfaReset(null)
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
    // Shown only once there is more than one population to distinguish, the same rule the filter above
    // and the create dialog's realm select follow: on a stock install every row would carry the same
    // word, which is a column that costs width and says nothing.
    ...(showRealm
      ? ([
          {
            key: 'realm',
            header: 'Realm',
            cell: (u) => <span className="text-sm text-text-2">{nameOf(u.realmId)}</span>,
          },
        ] satisfies DataListColumn<User>[])
      : []),
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
      key: 'mfa',
      header: '2FA',
      cell: (u) => <MfaBadge user={u} />,
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
          onResetMfa={() => setPendingMfaReset(u)}
          onToggleDisabled={() => setPendingToggle(u)}
          onDelete={() => setPendingDelete(u)}
        />
      ),
    },
  ]

  return (
    <div className="flex flex-col gap-6">
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

      {showRealm && (
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
          items={visibleUsers}
          getKey={(u) => u.id}
          columns={columns}
          renderCard={(u) => (
            <UserCard
              user={u}
              realmName={showRealm ? nameOf(u.realmId) : null}
              onEdit={() => setEditing(u)}
              onResetPassword={() => setResetting(u)}
              onResetMfa={() => setPendingMfaReset(u)}
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
            realms={realms}
            defaultRealmId={
              // Pre-select whatever the roster is filtered to — an administrator narrowing to a realm and
              // then adding an account almost always means "in this one".
              realmFilter === ALL_REALMS ? systemRealmId : Number(realmFilter)
            }
            isSystemRealm={isSystem}
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
              realmName={nameOf(editing.realmId)}
              canBeAdmin={isSystem(editing.realmId)}
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
        open={pendingMfaReset != null}
        onOpenChange={(open) => {
          if (!open) setPendingMfaReset(null)
        }}
        title={
          pendingMfaReset
            ? `Reset two-factor authentication for ${pendingMfaReset.userName}?`
            : 'Reset two-factor authentication?'
        }
        description="Their authenticator and every unused recovery code stop working, and they sign in with their password alone until they enrol again. Their sessions are left alone — this exists because someone cannot get in."
        confirmLabel="Reset"
        tone="danger"
        loading={resetMfa.isPending}
        onConfirm={() => {
          if (pendingMfaReset) resetMfa.mutate(pendingMfaReset.id)
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

/**
 * Whether the account carries a second factor. Worth a column of its own: it is what tells an
 * administrator whether "reset two-factor" has anything to do, and — read down the roster — how far the
 * instance actually is from having its accounts protected.
 */
function MfaBadge({ user }: { user: User }) {
  return user.twoFactorEnabled ? (
    <Badge tone="ok">On</Badge>
  ) : (
    <span className="text-sm text-text-3">Off</span>
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
  onResetMfa,
  onToggleDisabled,
  onDelete,
}: {
  user: User
  onEdit: () => void
  onResetPassword: () => void
  onResetMfa: () => void
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
        {/* Offered only when there is something to clear — on an account with no enrolment it would be an
            action whose whole effect is an audit row. */}
        {user.twoFactorEnabled && (
          <DropdownMenuItem onSelect={onResetMfa}>
            <ShieldOff /> Reset two-factor
          </DropdownMenuItem>
        )}
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
  realmName,
  onEdit,
  onResetPassword,
  onResetMfa,
  onToggleDisabled,
  onDelete,
}: {
  user: User
  /** Null on a single-realm install, where naming the one population everywhere says nothing. */
  realmName: string | null
  onEdit: () => void
  onResetPassword: () => void
  onResetMfa: () => void
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
        <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-text-3">
          <StatusBadge user={user} />
          {user.twoFactorEnabled && <Badge tone="ok" size="sm">2FA</Badge>}
          {realmName && <span>{realmName}</span>}
          <span className="tnum" title={absoluteTitle(user.createdAt)}>
            Created {timeAgo(user.createdAt)}
          </span>
        </div>
      </div>
      <RowActions
        user={user}
        onEdit={onEdit}
        onResetPassword={onResetPassword}
        onResetMfa={onResetMfa}
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
  realms,
  defaultRealmId,
  isSystemRealm,
  saving,
  onCancel,
  onSubmit,
}: {
  realms: Realm[]
  defaultRealmId: number
  isSystemRealm: (realmId: number) => boolean
  saving: boolean
  onCancel: () => void
  onSubmit: (data: CreateUserRequest) => void
}) {
  const [form, setForm] = useState<CreateUserRequest>({
    userName: '',
    password: '',
    email: '',
    isAdmin: false,
    realmId: defaultRealmId,
  })

  const realmId = form.realmId ?? defaultRealmId
  // Only an operator-realm account can administer the instance, and users.create refuses the pair
  // outright — so the toggle is not offered rather than offered and then rejected.
  const canBeAdmin = isSystemRealm(realmId)

  const canSubmit = form.userName.trim() !== '' && form.password !== ''

  return (
    <form
      className="mt-2 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (!canSubmit || saving) return
        onSubmit({
          ...form,
          userName: form.userName.trim(),
          email: form.email?.trim() || null,
          realmId,
          isAdmin: canBeAdmin && form.isAdmin,
        })
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

      {realms.length > 1 && (
        <Field
          label="Realm"
          hint="The population the account belongs to. Fixed once created — its user name is only unique within it."
        >
          {({ id, describedBy }) => (
            <Select
              value={String(realmId)}
              onValueChange={(v) => setForm((f) => ({ ...f, realmId: Number(v) }))}
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

      {canBeAdmin && (
        <AdminToggle
          checked={form.isAdmin}
          onChange={(isAdmin) => setForm((f) => ({ ...f, isAdmin }))}
        />
      )}

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
  realmName,
  canBeAdmin,
  saving,
  onCancel,
  onSubmit,
}: {
  user: User
  realmName: string
  /** Only an operator-realm account may hold the Admin role — users.update refuses the pair. */
  canBeAdmin: boolean
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

      <Field label="Realm" hint="An account never moves realm — its credentials belong to that population.">
        {({ id }) => <Input id={id} value={realmName} readOnly disabled />}
      </Field>

      {/* Hidden outside the operator realm: users.update refuses the Admin role there, so offering it
          would only produce a rejected save. */}
      {canBeAdmin && (
        <AdminToggle
          checked={form.isAdmin}
          onChange={(isAdmin) => setForm((f) => ({ ...f, isAdmin }))}
        />
      )}

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
