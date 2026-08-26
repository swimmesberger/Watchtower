import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Archive, ChevronDown, ChevronRight, History, Lock, Play } from 'lucide-react'
import { api } from '@/lib/api'
import type { BackupEvent, BackupPolicySource, BackupQuiesceMode, Stack } from '@/lib/types'
import { describeCron } from '@/lib/cron'
import { absoluteTitle, formatBytes, formatDuration, timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
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
import { StatusBadge } from '@/components/ui/status-badge'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'
import { cn } from '@/lib/utils'
import { BackupPlanPreviewSection } from './BackupPlanPreviewSection'

// ── Backups tab (ADR-0016) ──────────────────────────────────────────────────────
// Per-stack participation in the backup schedule (with an optional cron override, ADR-0018), a
// run-now action, and the run history. The storage target, the instance-wide schedule, retention
// and encryption live in Settings.
//
// Since ADR-0026 stage 7 every control here is one rung of a ladder: `compose label > stack override >
// template policy > instance default`. The switches show the *effective* value, and a chip beside each
// says which rung produced it. A tenant that inherits gets a "Use fleet policy" way back, because the
// only way to express "inherit" through a two-state switch is a separate control that clears the value.

export function StackBackupsTab({ stack }: { stack: Stack }) {
  const qc = useQueryClient()

  const {
    data: config,
    isLoading: configLoading,
    isError: configError,
    refetch: refetchConfig,
  } = useQuery({
    queryKey: ['backups', 'stack-config', stack.id],
    queryFn: () => api.backups.getStackConfig(stack.id),
  })

  // Instance-wide settings, for the "schedule is off" hint only — tolerate failure quietly.
  const { data: globalConfig } = useQuery({
    queryKey: ['backups', 'config'],
    queryFn: api.backups.getConfig,
    staleTime: 60_000,
    retry: false,
  })

  const { data: events = [], isLoading: eventsLoading } = useQuery({
    queryKey: ['backups', 'events', stack.id],
    queryFn: () => api.backups.events(stack.id),
    refetchInterval: (q) =>
      q.state.data?.some((e) => e.status === 'running' || e.status === 'queued') ? 3000 : 15_000,
  })

  const setConfig = useMutation({
    // Nullable throughout: the whole policy is posted on every call, and null clears a field so the
    // stack goes back to inheriting.
    mutationFn: (next: {
      enabled: boolean | null
      stopContainers: boolean | null
      cron: string | null
      quiesceMode: BackupQuiesceMode | null
    }) =>
      api.backups.setStackConfig(
        stack.id,
        next.enabled,
        next.stopContainers,
        next.cron,
        next.quiesceMode,
      ),
    onSuccess: (next) => {
      qc.setQueryData(['backups', 'stack-config', stack.id], next)
      toast.success('Backup settings saved.')
    },
    onError: (err: Error) => toast.error('Failed to save', err.message),
  })

  const runNow = useMutation({
    mutationFn: () => api.backups.run(stack.id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['backups', 'events', stack.id] })
      toast.info(`Backing up ${stack.name}…`)
    },
    onError: (err: Error) => toast.error('Backup failed to start', err.message),
  })

  // Restore flow: pick an archive from the storage (step 1), typed-name confirm (step 2).
  const [restoreOpen, setRestoreOpen] = useState(false)
  const [restoreFile, setRestoreFile] = useState<string | null>(null)
  const [restoreConfirmOpen, setRestoreConfirmOpen] = useState(false)

  const restore = useMutation({
    mutationFn: (fileName: string) => api.backups.restore(stack.id, fileName),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['backups', 'events', stack.id] })
      toast.info(`Restoring ${stack.name}…`, 'The stack is stopped while its volumes are refilled.')
    },
    onError: (err: Error) => toast.error('Restore failed to start', err.message),
    onSettled: () => {
      setRestoreConfirmOpen(false)
      setRestoreOpen(false)
      setRestoreFile(null)
    },
  })

  const isRunning = events.some((e) => e.status === 'running' || e.status === 'queued')

  return (
    <div className="space-y-4">
      <SectionHeader
        title="Backups"
        action={
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              size="sm"
              disabled={isRunning}
              onClick={() => setRestoreOpen(true)}
            >
              <History /> Restore…
            </Button>
            <Button
              variant="secondary"
              size="sm"
              loading={runNow.isPending || isRunning}
              disabled={runNow.isPending || isRunning}
              onClick={() => runNow.mutate()}
            >
              {!(runNow.isPending || isRunning) && <Play />}
              Back up now
            </Button>
          </div>
        }
      />
      <p className="-mt-2 text-[13px] text-text-2">
        Archives this stack’s volumes to the configured storage. The storage target, the instance
        schedule, retention and encryption are instance-wide —{' '}
        <Link to="/settings" className="underline underline-offset-2 hover:text-text">
          Settings → Backups
        </Link>
        .
      </p>

      {globalConfig && !globalConfig.enabled && (
        <Banner tone="warn" title="The backup schedule is off">
          Manual backups still work; enable the schedule under Settings → Backups for automatic runs.
        </Banner>
      )}

      {configLoading ? (
        <Card>
          <CardContent className="flex flex-col gap-4">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : configError || !config ? (
        <Banner
          tone="danger"
          title="Couldn’t load backup settings"
          action={
            <Button variant="secondary" size="sm" onClick={() => refetchConfig()}>
              Retry
            </Button>
          }
        />
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-5">
            {config.templateName && (
              <p className="text-[13px] text-text-2">
                This is an instance of{' '}
                <span className="font-medium text-text">{config.templateName}</span>. Anything left on
                the fleet policy follows that template and moves with it; anything set here stays set
                until it is cleared again.
              </p>
            )}

            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">
                  Include in the backup schedule
                  <SetBy source={config.enabledSource} templateName={config.templateName} />
                </span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Backs this stack up automatically on the schedule below.
                </span>
              </span>
              {/* A tenant gets three states, not two — see the remarks on InheritableToggle. */}
              <InheritableToggle
                label="Include in the backup schedule"
                own={config.ownEnabled}
                effective={config.enabled}
                templateName={config.templateName}
                inheritedFrom={config.enabledSource}
                onLabel="On"
                offLabel="Off"
                disabled={setConfig.isPending}
                onChange={(v) =>
                  setConfig.mutate({
                    enabled: v,
                    // The stack's *own* values for the fields this control is not touching. Sending
                    // the effective ones would silently turn every inherited field into an override
                    // the moment any one control is used.
                    stopContainers: config.ownStopContainers ?? null,
                    cron: config.ownCron ?? null,
                    quiesceMode: config.ownQuiesceMode ?? null,
                  })
                }
              />
            </label>

            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">
                  Stop stateful containers during the snapshot
                  <SetBy source={config.stopContainersSource} templateName={config.templateName} />
                </span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Stops only the containers that mount the volumes being archived (typically just
                  the database), dependents first, and restarts them in dependency order right
                  after. Postgres services are dumped with pg_dumpall instead and stay up. Off =
                  nothing is stopped; a write-active volume may be captured mid-write.
                </span>
              </span>
              <InheritableToggle
                label="Stop stateful containers during the snapshot"
                own={config.ownStopContainers}
                effective={config.stopContainers}
                templateName={config.templateName}
                inheritedFrom={config.stopContainersSource}
                onLabel="On"
                offLabel="Off"
                disabled={setConfig.isPending}
                onChange={(v) =>
                  setConfig.mutate({
                    enabled: config.ownEnabled ?? null,
                    stopContainers: v,
                    cron: config.ownCron ?? null,
                    quiesceMode: config.ownQuiesceMode ?? null,
                  })
                }
              />
            </label>

            <div className="flex flex-col gap-1.5">
              <label
                htmlFor="backup-quiesce-mode"
                className="text-[13px] font-medium text-text"
              >
                Quiesce mode
                <SetBy source={config.quiesceModeSource} templateName={config.templateName} />
              </label>
              <span className="text-[13px] text-text-2">
                How the stateful containers are taken out of service for the snapshot.{' '}
                <strong>Stop</strong> sends SIGTERM and restarts them afterwards — the application
                flushes and exits, so the snapshot is application-consistent. <strong>Pause</strong>{' '}
                freezes their processes for the duration of the tar (typically seconds) and thaws them
                — no restart, connections survive — but the snapshot is only{' '}
                <strong>crash-consistent</strong>: whatever an application still held in memory is not
                in it. Fine for file volumes (uploads, media); a database that cannot be dumped should
                keep <strong>Stop</strong>. A per-service{' '}
                <code className="font-mono text-[12px]">watchtower.backup.stop</code> label (
                <code className="font-mono text-[12px]">true</code> /{' '}
                <code className="font-mono text-[12px]">pause</code>) overrides this for that service.
              </span>
              <Select
                // A tenant selects among three: `inherit` when it has no value of its own, so the
                // control shows what is true rather than a concrete word it does not own. A standalone
                // stack has no Inherit row (see below), so it selects the effective value — giving it
                // `inherit` would set the trigger to an option that is not in the list, and Radix
                // renders that as an empty box.
                value={config.templateName ? (config.ownQuiesceMode ?? INHERIT) : config.quiesceMode}
                disabled={setConfig.isPending || !config.stopContainers}
                onValueChange={(v) =>
                  setConfig.mutate({
                    enabled: config.ownEnabled ?? null,
                    stopContainers: config.ownStopContainers ?? null,
                    cron: config.ownCron ?? null,
                    quiesceMode: v === INHERIT ? null : (v as BackupQuiesceMode),
                  })
                }
              >
                <SelectTrigger id="backup-quiesce-mode" className="max-w-[380px]">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {/* Offered only where inheriting is a distinct outcome. On a standalone stack the
                      ladder is stack-then-instance, so "inherit" and an explicit "stop" behave
                      identically and a third option would be a distinction without a difference. */}
                  {config.templateName && (
                    <SelectItem value={INHERIT}>
                      {inheritLabel(
                        config.quiesceMode === 'pause' ? 'Pause' : 'Stop',
                        config.quiesceModeSource,
                        config.templateName,
                      )}
                    </SelectItem>
                  )}
                  {/* Not "(default — …)": the instance default is only this stack's default when
                      nothing above it disagrees, and for a tenant of a pause fleet it does. The word
                      "default" belongs on the Inherit row, which names where the default comes from. */}
                  <SelectItem value="stop">Stop (application-consistent)</SelectItem>
                  <SelectItem value="pause">Pause (crash-consistent, seconds of downtime)</SelectItem>
                </SelectContent>
              </Select>
            </div>

            {/* Every field can be handed back one at a time through its own control; this is the
                all-at-once shortcut. Only offered when there is a fleet to hand it back to, and only
                when something is actually overridden — a button that clears nothing teaches nothing. */}
            {config.templateName && hasOwnValues(config) && (
              <div className="flex flex-wrap items-center gap-3">
                <Button
                  size="sm"
                  variant="ghost"
                  disabled={setConfig.isPending}
                  onClick={() =>
                    setConfig.mutate({
                      enabled: null,
                      stopContainers: null,
                      cron: null,
                      quiesceMode: null,
                    })
                  }
                >
                  Use {config.templateName}’s policy
                </Button>
                <span className="text-[13px] text-text-3">
                  Clears this instance’s own settings and follows the fleet again.
                </span>
              </div>
            )}

            <ScheduleOverride
              key={config.ownCron ?? ''}
              value={config.ownCron ?? null}
              effectiveCron={config.cron}
              cronSource={config.cronSource}
              templateName={config.templateName ?? null}
              instanceCron={globalConfig?.cron ?? null}
              pending={setConfig.isPending}
              onSave={(cron) =>
                setConfig.mutate({
                  enabled: config.ownEnabled ?? null,
                  stopContainers: config.ownStopContainers ?? null,
                  cron,
                  quiesceMode: config.ownQuiesceMode ?? null,
                })
              }
            />
          </CardContent>
        </Card>
      )}

      <BackupPlanPreviewSection stack={stack} />

      <SectionHeader title="History" />
      {eventsLoading ? (
        <div className="space-y-3">
          <Skeleton variant="rect" className="h-12 w-full" />
          <Skeleton variant="rect" className="h-12 w-full" />
        </div>
      ) : events.length === 0 ? (
        <EmptyState
          icon={Archive}
          title="No backups yet"
          description="Run one with “Back up now”, or include this stack in the backup schedule."
        />
      ) : (
        <div className="space-y-2">
          {events.map((e) => (
            <BackupHistoryRow key={e.id} event={e} />
          ))}
        </div>
      )}

      {/* Step 1 — pick an archive from the storage. */}
      <RestoreSelectDialog
        open={restoreOpen}
        onOpenChange={(o) => {
          setRestoreOpen(o)
          if (!o) setRestoreFile(null)
        }}
        stack={stack}
        selected={restoreFile}
        onSelect={setRestoreFile}
        onContinue={() => {
          setRestoreOpen(false)
          setRestoreConfirmOpen(true)
        }}
      />

      {/* Step 2 — typed-name confirm: restoring overwrites the volumes' current data. */}
      <ConfirmDialog
        open={restoreConfirmOpen}
        onOpenChange={(o) => {
          setRestoreConfirmOpen(o)
          if (!o) setRestoreOpen(true)
        }}
        title={`Restore ${stack.name} from a backup?`}
        description={
          <span>
            This stops the stack, <strong>erases the current contents</strong> of every volume in the
            archive, refills them from the backup, and restarts the stack. Data written since the
            backup was taken is lost. <strong>This cannot be undone.</strong>
            {restoreFile && (
              <span className="mt-2 block font-mono text-[12px] text-text">{restoreFile}</span>
            )}
          </span>
        }
        confirmLabel="Erase & restore"
        tone="danger"
        requireText={stack.name}
        loading={restore.isPending}
        onConfirm={() => restoreFile && restore.mutate(restoreFile)}
      />
    </div>
  )
}

/** The sentinel a tri-state control uses for "no opinion" — the empty string is not a select value. */
const INHERIT = 'inherit'

/**
 * The Inherit row's text.
 *
 * **It may only name a value when that value is the one inheriting would actually give.** While the
 * field *is* inherited, the effective value and the inherited value are the same thing, so the row
 * says which and from where — that is what makes "Inherit" an option whose consequence the reader can
 * see. While the stack **overrides** the field, the effective value is the stack's own and naming it
 * here would promise that picking Inherit changes nothing, when it is precisely the control that
 * changes it back. The wire carries no "what you would inherit" value (the resolver stops at the
 * winner), so the row drops the parenthetical rather than inventing one: it says where the answer
 * would come from, and the reader sees the new value the moment they pick it.
 */
function inheritLabel(
  effectiveValue: string,
  source: BackupPolicySource,
  templateName: string | null | undefined,
): string {
  const from = source === 'template' && templateName ? templateName : 'instance default'
  return source === 'stack'
    ? `Inherit from ${templateName ?? 'the instance default'}`
    : `Inherit (currently: ${effectiveValue} — from ${from})`
}

/**
 * A nullable boolean control.
 *
 * **For a tenant this must be three states, not two.** A `Switch` can only render on or off, so an
 * inherited `true` looks exactly like an owned `true` — and the two are not the same thing: toggling
 * such a switch twice, or "confirming" the value already on screen, silently detaches the field from
 * the fleet and freezes it at whatever the fleet happened to say that day. The select renders the
 * inherited state *as inherited* and names the value in force, so choosing what is on screen is a
 * no-op and choosing anything else is visibly a decision. Picking Inherit is also the per-field
 * revert, which is why there is no separate revert affordance per row.
 *
 * **For a standalone stack it stays the switch it has always been.** There the ladder is
 * stack-then-instance, so "inherit" and an explicit value that equals the instance default behave
 * identically — a third state would be a distinction the reader cannot act on, on a page a hobby
 * install has been reading unchanged since ADR-0016.
 */
function InheritableToggle({
  label,
  own,
  effective,
  templateName,
  inheritedFrom,
  onLabel,
  offLabel,
  disabled,
  onChange,
}: {
  label: string
  /** What the stack itself says; null/undefined means it inherits. */
  own: boolean | null | undefined
  /** What is actually in force right now — what the Inherit row has to name. */
  effective: boolean
  templateName: string | null | undefined
  inheritedFrom: BackupPolicySource
  onLabel: string
  offLabel: string
  disabled: boolean
  onChange: (value: boolean | null) => void
}) {
  if (!templateName) {
    return (
      <Switch
        checked={effective}
        disabled={disabled}
        onCheckedChange={onChange}
        aria-label={label}
      />
    )
  }
  return (
    <Select
      value={own != null ? String(own) : INHERIT}
      disabled={disabled}
      onValueChange={(v) => onChange(v === INHERIT ? null : v === 'true')}
    >
      <SelectTrigger aria-label={label} className="w-[300px] shrink-0">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={INHERIT}>
          {inheritLabel(effective ? onLabel : offLabel, inheritedFrom, templateName)}
        </SelectItem>
        <SelectItem value="true">{onLabel}</SelectItem>
        <SelectItem value="false">{offLabel}</SelectItem>
      </SelectContent>
    </Select>
  )
}

/**
 * Where an effective backup setting came from. A chip beside the control rather than prose, because
 * the reader's question is "is this mine or the fleet's?" and the answer has to be legible without
 * reading a sentence.
 *
 * Two things it deliberately does not say. Nothing for a value the stack set itself — a control
 * showing a value the stack owns already means that. And **nothing at all on a standalone stack**,
 * where the ladder has only one rung below the labels: a hobby install would otherwise grow a "Set
 * by: instance default" chip on every row of a page that has not changed, which is exactly the kind
 * of noise design.md's UX bar rules out. Provenance is only information where there is more than one
 * possible answer.
 */
function SetBy({
  source,
  templateName,
}: {
  source: BackupPolicySource
  templateName?: string | null
}) {
  if (source === 'stack' || !templateName) return null
  return (
    <Badge tone="neutral" size="sm" className="ml-2 align-middle font-normal">
      {source === 'template' ? `Set by: ${templateName}` : 'Set by: instance default'}
    </Badge>
  )
}

/** True when the stack overrides at least one field — i.e. there is something to hand back. */
function hasOwnValues(config: {
  ownEnabled?: boolean | null
  ownStopContainers?: boolean | null
  ownCron?: string | null
  ownQuiesceMode?: BackupQuiesceMode | null
}): boolean {
  // `!= null` throughout: the API omits nulls, so an unset field arrives as `undefined`.
  return (
    config.ownEnabled != null ||
    config.ownStopContainers != null ||
    config.ownCron != null ||
    config.ownQuiesceMode != null
  )
}

/**
 * This stack's own cron expression (ADR-0018). Empty means it inherits — the fleet's expression when
 * the stack is a tenant of a template that sets one, otherwise the instance schedule — so the preview
 * names whichever of those actually applies rather than always saying "instance". The server validates
 * the expression and reports a bad one through the mutation's error toast.
 */
function ScheduleOverride({
  value,
  effectiveCron,
  cronSource,
  templateName,
  instanceCron,
  pending,
  onSave,
}: {
  value: string | null
  effectiveCron: string | null
  cronSource: BackupPolicySource
  templateName: string | null
  instanceCron: string | null
  pending: boolean
  onSave: (cron: string | null) => void
}) {
  const [draft, setDraft] = useState(value ?? '')
  const trimmed = draft.trim()
  const dirty = trimmed !== (value ?? '')
  const preview =
    trimmed.length === 0 && cronSource === 'template'
      ? {
        text: `Follows ${templateName ?? 'the fleet policy'}: `
          + `${effectiveCron === null
            ? 'the instance schedule'
            : describeCron(effectiveCron) ?? effectiveCron}.`,
        invalid: false,
      }
      : describeSchedule(trimmed, instanceCron)

  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-[13px] font-medium text-text">
        Schedule override
        <SetBy source={cronSource} templateName={templateName} />
      </span>
      <span className="text-[13px] text-text-2">
        Runs this stack on its own cron expression instead of the inherited one — five fields
        (minute hour day-of-month month day-of-week), server-local time. Leave empty to inherit.
      </span>
      <div className="flex flex-wrap items-center gap-2">
        <Input
          mono
          className="max-w-[220px]"
          placeholder={instanceCron ?? '30 3 * * *'}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          aria-label="Backup schedule override for this stack"
        />
        <Button
          size="sm"
          variant="secondary"
          loading={pending}
          disabled={!dirty || pending}
          onClick={() => onSave(trimmed || null)}
        >
          Save
        </Button>
        {value !== null && (
          <Button
            size="sm"
            variant="ghost"
            disabled={pending}
            onClick={() => {
              setDraft('')
              onSave(null)
            }}
          >
            {templateName ? 'Use the inherited schedule' : 'Use instance schedule'}
          </Button>
        )}
      </div>
      <span className={cn('text-[13px]', preview.invalid ? 'text-danger' : 'text-text-2')}>
        {preview.text}
      </span>
    </div>
  )
}

/** The preview line under the override input: this stack's schedule, in words where possible. */
function describeSchedule(
  draft: string,
  instanceCron: string | null,
): { text: string; invalid: boolean } {
  if (draft.length === 0) {
    const instance = instanceCron === null ? null : describeCron(instanceCron) ?? instanceCron
    return {
      text: instance === null
        ? 'Follows the instance schedule.'
        : `Follows the instance schedule: ${instance}.`,
      invalid: false,
    }
  }
  if (draft.split(/\s+/).filter((f) => f.length > 0).length !== 5) {
    return {
      text: 'Needs exactly five fields: minute hour day-of-month month day-of-week.',
      invalid: true,
    }
  }
  const described = describeCron(draft)
  return {
    text: described === null
      ? 'Custom expression — shown as entered.'
      : described.charAt(0).toUpperCase() + described.slice(1),
    invalid: false,
  }
}

/** Step 1 of the restore flow: the archives actually present on the storage, newest first. */
function RestoreSelectDialog({
  open,
  onOpenChange,
  stack,
  selected,
  onSelect,
  onContinue,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  stack: Stack
  selected: string | null
  onSelect: (name: string) => void
  onContinue: () => void
}) {
  const { data: files, isLoading, isError, error } = useQuery({
    queryKey: ['backups', 'remote', stack.id],
    queryFn: () => api.backups.listRemote(stack.id),
    enabled: open,
    staleTime: 30_000,
    retry: false,
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Restore {stack.name}</DialogTitle>
          <DialogDescription>
            Choose a backup from the storage. The list shows what is actually there right now.
          </DialogDescription>
        </DialogHeader>

        {isLoading ? (
          <div className="space-y-2">
            <Skeleton variant="rect" className="h-10 w-full" />
            <Skeleton variant="rect" className="h-10 w-full" />
          </div>
        ) : isError ? (
          <Banner tone="danger" title="Couldn’t list the backup storage">
            {error instanceof Error ? error.message : 'The storage is unreachable.'}
          </Banner>
        ) : !files || files.length === 0 ? (
          <p className="text-sm text-text-3">No backups of this stack exist on the storage yet.</p>
        ) : (
          <div className="flex max-h-[40dvh] flex-col gap-1 overflow-y-auto">
            {files.map((f) => (
              <label
                key={f.name}
                className="flex cursor-pointer items-center gap-3 rounded-md border border-border px-3 py-2 hover:bg-surface-2"
              >
                <input
                  type="radio"
                  name="restore-file"
                  checked={selected === f.name}
                  onChange={() => onSelect(f.name)}
                  className="size-4 shrink-0 accent-[var(--brand)]"
                />
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-mono text-[12.5px] text-text">{f.name}</span>
                  <span className="tnum block text-[12px] text-text-2" title={absoluteTitle(f.takenAt)}>
                    {timeAgo(f.takenAt)} · {formatBytes(f.sizeBytes)}
                  </span>
                </span>
                {f.encrypted && (
                  <span
                    className="inline-flex items-center gap-1 text-[11px] text-text-3"
                    title="Encrypted — restore uses the configured passphrase."
                  >
                    <Lock className="size-3 shrink-0" aria-hidden />
                    encrypted
                  </span>
                )}
              </label>
            ))}
          </div>
        )}

        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button variant="danger" disabled={!selected} onClick={onContinue}>
            Continue
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** One expandable history row: status, trigger, age, size — expanded shows the run log. */
function BackupHistoryRow({ event }: { event: BackupEvent }) {
  const [expanded, setExpanded] = useState(false)
  const isActive = event.status === 'running' || event.status === 'queued'

  return (
    <div className="overflow-hidden rounded-lg border border-border bg-surface">
      <button
        type="button"
        onClick={() => setExpanded((e) => !e)}
        aria-expanded={expanded}
        className={cn(
          'flex w-full flex-wrap items-center gap-x-3 gap-y-1.5 px-4 py-3 text-left',
          'transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
        )}
      >
        {expanded ? (
          <ChevronDown className="size-4 shrink-0 text-text-3" aria-hidden />
        ) : (
          <ChevronRight className="size-4 shrink-0 text-text-3" aria-hidden />
        )}
        <StatusBadge status={event.status} size="sm" />
        <span className="rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] font-medium text-text-2">
          {event.triggeredBy}
        </span>
        <span className="tnum text-xs text-text-2" title={absoluteTitle(event.startedAt)}>
          {timeAgo(event.startedAt)}
        </span>
        {event.sizeBytes != null && (
          <span className="tnum text-xs text-text-2">{formatBytes(event.sizeBytes)}</span>
        )}
        <span className="tnum ml-auto text-xs text-text-3">
          {formatDuration(event.startedAt, event.finishedAt)}
        </span>
        {isActive && (
          <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold text-run">
            <span
              className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]"
              aria-hidden
            />
            live
          </span>
        )}
      </button>

      {expanded && (
        <div className="border-t border-border p-3">
          {event.remotePath && (
            <p className="mb-2 break-all font-mono text-[12px] text-text-2">{event.remotePath}</p>
          )}
          <pre className="max-h-72 overflow-auto whitespace-pre-wrap rounded-md bg-surface-2 p-3 font-mono text-[12px] leading-relaxed text-text-2">
            {event.output ?? (isActive ? 'Running…' : 'No output recorded.')}
          </pre>
        </div>
      )}
    </div>
  )
}
