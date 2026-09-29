import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, Trash2, X } from 'lucide-react'
import { api } from '@/lib/api'
import type {
  AccessClauseKind,
  AccessRule,
  AccessRuleClause,
  ActiveEnforcementPoint,
} from '@/lib/types'
import { Badge, type BadgeTone } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
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
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

/**
 * The clause kinds in menu order, with what each one is called in the UI and whether it names a Watchtower
 * subject or somebody else's (ADR-0039). The two portable kinds come first: they are the ones that keep
 * meaning the same if the operator ever changes provider.
 */
interface ClauseKindMeta {
  value: AccessClauseKind
  label: string
  hint: string
  /** Which control the clause needs: a roster picker, or a free-text value. */
  subject: 'user' | 'group' | 'value'
  placeholder?: string
}

const CLAUSE_KINDS: [ClauseKindMeta, ...ClauseKindMeta[]] = [
  {
    value: 'Group',
    label: 'Watchtower group',
    hint: 'Everyone in the group. Evaluated per request by Watchtower, flattened to member addresses at the Cloudflare edge.',
    subject: 'group',
  },
  {
    value: 'User',
    label: 'Watchtower user',
    hint: 'One account. At the Cloudflare edge this is its email address, so an account without one admits nobody there.',
    subject: 'user',
  },
  {
    value: 'Email',
    label: 'Email address',
    hint: 'One address, matched by Cloudflare Access. Not enforceable by Watchtower’s own forward-auth, which does not verify addresses.',
    subject: 'value',
    placeholder: 'friend@example.com',
  },
  {
    value: 'EmailDomain',
    label: 'Email domain',
    hint: 'Every address at one domain, matched by Cloudflare Access.',
    subject: 'value',
    placeholder: 'example.com',
  },
  {
    value: 'ExternalGroup',
    label: 'Cloudflare Access group',
    hint: 'An Access group id from your Cloudflare account — the fit when the allow-list already lives there.',
    subject: 'value',
    placeholder: 'Access group id',
  },
  {
    value: 'ExternalPolicy',
    label: 'Cloudflare reusable policy',
    hint: 'A reusable Access policy you maintain in the Cloudflare dashboard. Watchtower attaches it and never edits it.',
    subject: 'value',
    placeholder: 'Reusable policy id',
  },
]

/**
 * The metadata for one clause kind. Falls back to the first entry rather than returning undefined: a kind
 * from a newer backend should render as *something* rather than crash the dialog that would let an operator
 * remove it.
 */
function kindMeta(kind: AccessClauseKind): ClauseKindMeta {
  return CLAUSE_KINDS.find((k) => k.value === kind) ?? CLAUSE_KINDS[0]
}

/**
 * Where a rule can be enforced, as one badge. Deliberately stated rather than implied: a rule that only the
 * Cloudflare edge can honour is refused when attached under the in-process proxy, and the reason an operator
 * can see beforehand is this badge (ADR-0039 decision 4).
 */
function portabilityBadge(rule: AccessRule): { tone: BadgeTone; label: string; hint: string } {
  if (rule.enforceableInProcess && rule.enforceableByCloudflareAccess) {
    return {
      tone: 'ok',
      label: 'Any provider',
      hint: 'Every clause means the same thing wherever this route is served.',
    }
  }
  if (rule.enforceableByCloudflareAccess) {
    return {
      tone: 'neutral',
      label: 'Cloudflare only',
      hint: 'Some clauses can only be enforced by Cloudflare Access, so this rule cannot be attached under the built-in proxy.',
    }
  }
  if (rule.enforceableInProcess) {
    return {
      tone: 'neutral',
      label: 'Built-in proxy only',
      hint: 'Some clauses can only be enforced by Watchtower’s own forward-auth.',
    }
  }
  return {
    tone: 'warn',
    label: 'Not enforceable',
    hint: 'No provider can honour every clause in this rule, so attaching it would admit fewer people than it says.',
  }
}

/** Whether the active provider can honour every clause of a rule — the attachability question. */
export function isAttachableAt(rule: AccessRule, point: ActiveEnforcementPoint): boolean {
  return point === 'CloudflareAccess' ? rule.enforceableByCloudflareAccess : rule.enforceableInProcess
}

/**
 * Manages the named access rules a route can attach (ADR-0039) — the indirection that lets one hostname admit
 * "family and friends" while another admits only "family", and lets a clause be swapped later without
 * touching a single route.
 */
export function AccessRulesCard({ realmId }: { realmId?: number }) {
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<AccessRule | 'new' | null>(null)
  const [deleting, setDeleting] = useState<AccessRule | null>(null)

  const { data: rules = [], isLoading } = useQuery({
    queryKey: ['access-rules', { realmId }],
    queryFn: () => api.proxy.listAccessRules(realmId),
  })

  const remove = useMutation({
    mutationFn: (rule: AccessRule) => api.proxy.deleteAccessRule(rule.id),
    onSuccess: async (_, rule) => {
      toast.success(`Access rule “${rule.name}” deleted.`)
      setDeleting(null)
      await queryClient.invalidateQueries({ queryKey: ['access-rules'] })
    },
    // The refusal names the routes still attaching it, which is the actionable part.
    onError: (err: Error) => toast.error(err.message || 'Failed to delete the access rule.'),
  })

  return (
    <>
      <Card>
        <CardContent className="flex flex-col gap-3">
          <SectionHeader
            title="Access rules"
            description="Named allow-lists a protected route can attach instead of using the instance-wide settings. Attach two to admit both — your own people and a wider group — on one hostname."
            action={
              <Button size="sm" variant="secondary" onClick={() => setEditing('new')}>
                <Plus className="size-4" />
                New rule
              </Button>
            }
          />

          {isLoading ? (
            <Skeleton className="h-16 w-full" />
          ) : rules.length === 0 ? (
            <p className="text-[13px] text-text-3">
              No rules yet. A protected route with none falls back to the allow sources configured under
              Settings → Reverse proxy, which apply to every protected hostname alike.
            </p>
          ) : (
            <div className="flex flex-col divide-y divide-border rounded-md border border-border">
              {rules.map((rule) => {
                const badge = portabilityBadge(rule)
                return (
                  <div key={rule.id} className="flex items-start gap-3 px-3 py-2.5">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-sm font-medium text-text">{rule.name}</span>
                        <Tooltip label={badge.hint}>
                          <Badge tone={badge.tone}>{badge.label}</Badge>
                        </Tooltip>
                        <span className="text-xs text-text-3">
                          {rule.attachedRouteCount === 0
                            ? 'not attached'
                            : rule.attachedRouteCount === 1
                              ? 'on 1 route'
                              : `on ${rule.attachedRouteCount} routes`}
                        </span>
                      </div>
                      {rule.description && (
                        <p className="mt-0.5 text-xs text-text-3">{rule.description}</p>
                      )}
                      <p className="mt-1 text-xs text-text-3">
                        {rule.clauses.length === 0
                          ? 'No clauses — admits nobody.'
                          : rule.clauses
                              .map((c) => `${kindMeta(c.kind).label}: ${c.subjectLabel ?? c.value ?? '?'}`)
                              .join(' · ')}
                      </p>
                    </div>
                    <div className="flex shrink-0 gap-1">
                      <Button size="sm" variant="ghost" onClick={() => setEditing(rule)}>
                        Edit
                      </Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        aria-label={`Delete ${rule.name}`}
                        onClick={() => setDeleting(rule)}
                      >
                        <Trash2 className="size-4" />
                      </Button>
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </CardContent>
      </Card>

      <AccessRuleDialog
        rule={editing}
        realmId={realmId}
        onClose={() => setEditing(null)}
        onSaved={async () => {
          setEditing(null)
          // Both: a rule's contents change what every route attaching it admits.
          await queryClient.invalidateQueries({ queryKey: ['access-rules'] })
          await queryClient.invalidateQueries({ queryKey: ['routes'] })
        }}
      />

      <ConfirmDialog
        open={deleting != null}
        onOpenChange={(o) => !o && !remove.isPending && setDeleting(null)}
        title={`Delete “${deleting?.name ?? ''}”?`}
        description="The rule and its clauses go. Routes that still attach it must be detached first — the delete is refused while any do, naming them."
        confirmLabel="Delete"
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting)}
      />
    </>
  )
}

function AccessRuleDialog({
  rule,
  realmId,
  onClose,
  onSaved,
}: {
  rule: AccessRule | 'new' | null
  realmId?: number
  onClose: () => void
  onSaved: () => void
}) {
  const open = rule != null
  const existing = rule === 'new' ? null : rule

  const save = useMutation({
    mutationFn: (data: { name: string; description: string | null; clauses: AccessRuleClause[] }) =>
      api.proxy.setAccessRule({
        id: existing?.id ?? null,
        name: data.name,
        description: data.description,
        realmId: existing?.realmId ?? realmId ?? null,
        clauses: data.clauses,
      }),
    onSuccess: () => {
      toast.success(existing ? 'Access rule updated.' : 'Access rule created.')
      onSaved()
    },
    // Every validation refusal — a malformed address, a cross-realm account — arrives as the error text.
    onError: (err: Error) => toast.error(err.message || 'Failed to save the access rule.'),
  })

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !save.isPending && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{existing ? `Access rule · ${existing.name}` : 'New access rule'}</DialogTitle>
          <DialogDescription>
            A rule is a list of clauses, and it admits anyone matching any of them. Routes attach the rule by
            name, so editing it here changes every hostname that names it.
          </DialogDescription>
        </DialogHeader>
        {open && (
          <AccessRuleForm
            key={existing?.id ?? 'new'}
            initial={existing}
            realmId={existing?.realmId ?? realmId}
            saving={save.isPending}
            onCancel={onClose}
            onSubmit={(data) => save.mutate(data)}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

function AccessRuleForm({
  initial,
  realmId,
  saving,
  onCancel,
  onSubmit,
}: {
  initial: AccessRule | null
  realmId?: number
  saving: boolean
  onCancel: () => void
  onSubmit: (data: { name: string; description: string | null; clauses: AccessRuleClause[] }) => void
}) {
  const [name, setName] = useState(initial?.name ?? '')
  const [description, setDescription] = useState(initial?.description ?? '')
  const [clauses, setClauses] = useState<AccessRuleClause[]>(initial?.clauses ?? [])
  const [pendingKind, setPendingKind] = useState<AccessClauseKind>('ExternalPolicy')

  // The rosters the two Watchtower-subject kinds pick from, scoped to the rule's realm for the reason the
  // grant pickers are: a clause naming another population is refused on save and would admit nobody anyway.
  const { data: users = [] } = useQuery({
    queryKey: ['users', { realmId }],
    queryFn: () => api.users.list(realmId),
  })
  const { data: groups = [] } = useQuery({
    queryKey: ['groups', { realmId }],
    queryFn: () => api.groups.list(realmId),
  })

  // The account's own reusable policies, so an external-policy clause is picked by name rather than by
  // pasting a UUID. Fails open: an empty roster still leaves the free-text field usable.
  const { data: external } = useQuery({
    queryKey: ['external-access-policies'],
    queryFn: () => api.proxy.listExternalAccessPolicies(),
  })

  const meta = kindMeta(pendingKind)

  function addClause(clause: AccessRuleClause) {
    setClauses((current) => {
      const duplicate = current.some(
        (c) =>
          c.kind === clause.kind &&
          c.userId === clause.userId &&
          c.groupId === clause.groupId &&
          (c.value ?? '').toLowerCase() === (clause.value ?? '').toLowerCase(),
      )
      return duplicate ? current : [...current, clause]
    })
  }

  return (
    <form
      className="mt-1 flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (saving) return
        onSubmit({
          name: name.trim(),
          description: description.trim() === '' ? null : description.trim(),
          clauses,
        })
      }}
    >
      <Field label="Name" hint="What you will pick this rule by on a route. Unique within the realm.">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="friends"
            maxLength={64}
            required
          />
        )}
      </Field>

      <Field label="Description" hint="Optional — what this rule is for, shown beside its name.">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="the people I let into the shared apps"
            maxLength={256}
          />
        )}
      </Field>

      <Field label="Clauses" hint="Anyone matching any clause gets in.">
        {() => (
          <div className="flex flex-col gap-2">
            {clauses.length === 0 ? (
              <p className="text-[13px] text-text-3">
                No clauses yet. A rule with none admits nobody, and a route attaching it is locked out
                rather than silently falling back to the instance-wide settings.
              </p>
            ) : (
              <div className="flex flex-col divide-y divide-border rounded-md border border-border">
                {clauses.map((clause, index) => (
                  <div key={index} className="flex items-center gap-2 px-3 py-2">
                    <span className="min-w-0 flex-1 text-sm text-text">
                      <span className="text-text-3">{kindMeta(clause.kind).label}: </span>
                      {clause.subjectLabel ?? clause.value ?? '?'}
                    </span>
                    <Button
                      size="sm"
                      variant="ghost"
                      aria-label="Remove clause"
                      onClick={() => setClauses((c) => c.filter((_, i) => i !== index))}
                    >
                      <X className="size-4" />
                    </Button>
                  </div>
                ))}
              </div>
            )}

            <div className="flex flex-col gap-2 rounded-md border border-border bg-surface-2 p-3">
              <Select value={pendingKind} onValueChange={(v) => setPendingKind(v as AccessClauseKind)}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {CLAUSE_KINDS.map((k) => (
                    <SelectItem key={k.value} value={k.value}>
                      {k.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <p className="text-xs text-text-3">{meta.hint}</p>

              {meta.subject === 'user' && (
                <SubjectPicker
                  empty="No accounts in this realm yet."
                  options={users.map((u) => ({ id: u.id, label: u.userName, note: u.email ?? undefined }))}
                  onPick={(id, label) =>
                    addClause({ kind: 'User', userId: id, subjectLabel: label })
                  }
                />
              )}

              {meta.subject === 'group' && (
                <SubjectPicker
                  empty="No groups in this realm yet."
                  options={groups.map((g) => ({
                    id: g.id,
                    label: g.name,
                    note: g.memberCount === 1 ? '1 member' : `${g.memberCount} members`,
                  }))}
                  onPick={(id, label) =>
                    addClause({ kind: 'Group', groupId: id, subjectLabel: label })
                  }
                />
              )}

              {meta.subject === 'value' && (
                <ValueAdder
                  placeholder={meta.placeholder ?? 'Value'}
                  // Only the reusable-policy kind has a roster to offer; the others are typed in.
                  suggestions={
                    pendingKind === 'ExternalPolicy'
                      ? (external?.policies ?? []).map((p) => ({
                          value: p.id,
                          label: p.decision ? `${p.name} (${p.decision})` : p.name,
                        }))
                      : []
                  }
                  onAdd={(value, label) =>
                    addClause({ kind: pendingKind, value, subjectLabel: label ?? value })
                  }
                />
              )}

              {pendingKind === 'ExternalPolicy' && external?.warning && (
                <Banner tone="info" title="No policy list">
                  {external.warning}
                </Banner>
              )}
            </div>
          </div>
        )}
      </Field>

      <div className="flex justify-end gap-2 pt-1">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" loading={saving}>
          Save
        </Button>
      </div>
    </form>
  )
}

/** Picks one Watchtower subject out of a roster and adds it as a clause. */
function SubjectPicker({
  options,
  empty,
  onPick,
}: {
  options: { id: number; label: string; note?: string }[]
  empty: string
  onPick: (id: number, label: string) => void
}) {
  const [selected, setSelected] = useState<string>('')
  const chosen = useMemo(
    () => options.find((o) => String(o.id) === selected),
    [options, selected],
  )

  if (options.length === 0) return <p className="text-[13px] text-text-3">{empty}</p>

  return (
    <div className="flex gap-2">
      <Select value={selected} onValueChange={setSelected}>
        <SelectTrigger className="flex-1">
          <SelectValue placeholder="Pick one…" />
        </SelectTrigger>
        <SelectContent>
          {options.map((o) => (
            <SelectItem key={o.id} value={String(o.id)}>
              {o.note ? `${o.label} — ${o.note}` : o.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      <Button
        type="button"
        variant="secondary"
        disabled={!chosen}
        onClick={() => {
          if (!chosen) return
          onPick(chosen.id, chosen.label)
          setSelected('')
        }}
      >
        Add
      </Button>
    </div>
  )
}

/**
 * Adds a value-carrying clause: picked from a roster when there is one (the account's reusable policies), or
 * typed in. Both, rather than one or the other, because the roster does not paginate and an operator who
 * knows the id must always be able to enter it.
 */
function ValueAdder({
  placeholder,
  suggestions,
  onAdd,
}: {
  placeholder: string
  suggestions: { value: string; label: string }[]
  onAdd: (value: string, label?: string) => void
}) {
  const [typed, setTyped] = useState('')
  const [picked, setPicked] = useState<string>('')

  return (
    <div className="flex flex-col gap-2">
      {suggestions.length > 0 && (
        <div className="flex gap-2">
          <Select value={picked} onValueChange={setPicked}>
            <SelectTrigger className="flex-1">
              <SelectValue placeholder="Pick from your Cloudflare account…" />
            </SelectTrigger>
            <SelectContent>
              {suggestions.map((s) => (
                <SelectItem key={s.value} value={s.value}>
                  {s.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            type="button"
            variant="secondary"
            disabled={picked === ''}
            onClick={() => {
              const match = suggestions.find((s) => s.value === picked)
              if (!match) return
              onAdd(match.value, match.label)
              setPicked('')
            }}
          >
            Add
          </Button>
        </div>
      )}
      <div className="flex gap-2">
        <Input
          className="flex-1"
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder={placeholder}
          maxLength={320}
          spellCheck={false}
        />
        <Button
          type="button"
          variant="secondary"
          disabled={typed.trim() === ''}
          onClick={() => {
            onAdd(typed.trim())
            setTyped('')
          }}
        >
          Add
        </Button>
      </div>
    </div>
  )
}
