import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Archive, ChevronDown, ChevronRight, History, Lock, Play } from 'lucide-react'
import { api } from '@/lib/api'
import type { BackupEvent, Stack } from '@/lib/types'
import { describeCron } from '@/lib/cron'
import { absoluteTitle, formatBytes, formatDuration, timeAgo } from '@/lib/format'
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
import { Skeleton } from '@/components/ui/skeleton'
import { StatusBadge } from '@/components/ui/status-badge'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'
import { cn } from '@/lib/utils'

// ── Backups tab (ADR-0016) ──────────────────────────────────────────────────────
// Per-stack participation in the backup schedule (with an optional cron override, ADR-0018), a
// run-now action, and the run history. The storage target, the instance-wide schedule, retention
// and encryption live in Settings.

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
    mutationFn: (next: { enabled: boolean; stopContainers: boolean; cron: string | null }) =>
      api.backups.setStackConfig(stack.id, next.enabled, next.stopContainers, next.cron),
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
            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">
                  Include in the backup schedule
                </span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Backs this stack up automatically on the schedule below.
                </span>
              </span>
              <Switch
                checked={config.enabled}
                disabled={setConfig.isPending}
                onCheckedChange={(v) =>
                  setConfig.mutate({
                    enabled: v,
                    stopContainers: config.stopContainers,
                    cron: config.cron,
                  })
                }
                aria-label="Include in the backup schedule"
              />
            </label>

            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">
                  Stop stateful containers during the snapshot
                </span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Stops only the containers that mount the volumes being archived (typically just
                  the database), dependents first, and restarts them in dependency order right
                  after. Postgres services are dumped with pg_dumpall instead and stay up. Off =
                  nothing is stopped; a write-active volume may be captured mid-write.
                </span>
              </span>
              <Switch
                checked={config.stopContainers}
                disabled={setConfig.isPending}
                onCheckedChange={(v) =>
                  setConfig.mutate({
                    enabled: config.enabled,
                    stopContainers: v,
                    cron: config.cron,
                  })
                }
                aria-label="Stop stateful containers during the snapshot"
              />
            </label>

            <ScheduleOverride
              key={config.cron ?? ''}
              value={config.cron}
              instanceCron={globalConfig?.cron ?? null}
              pending={setConfig.isPending}
              onSave={(cron) =>
                setConfig.mutate({
                  enabled: config.enabled,
                  stopContainers: config.stopContainers,
                  cron,
                })
              }
            />
          </CardContent>
        </Card>
      )}

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

/**
 * This stack's own cron expression (ADR-0018). Empty means it follows the instance schedule, so the
 * placeholder and the preview both show what that currently is. The server validates the expression
 * and reports a bad one through the mutation's error toast.
 */
function ScheduleOverride({
  value,
  instanceCron,
  pending,
  onSave,
}: {
  value: string | null
  instanceCron: string | null
  pending: boolean
  onSave: (cron: string | null) => void
}) {
  const [draft, setDraft] = useState(value ?? '')
  const trimmed = draft.trim()
  const dirty = trimmed !== (value ?? '')
  const preview = describeSchedule(trimmed, instanceCron)

  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-[13px] font-medium text-text">Schedule override</span>
      <span className="text-[13px] text-text-2">
        Runs this stack on its own cron expression instead of the instance schedule — five fields
        (minute hour day-of-month month day-of-week), server-local time. Leave empty to follow the
        instance.
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
            Use instance schedule
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
