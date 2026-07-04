import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  RefreshCw,
  RotateCcw,
  Timer,
} from 'lucide-react'
import { api } from '@/lib/api'
import type { SelfUpdateStatus, UpdateSelfConfigRequest } from '@/lib/types'
import { absoluteTitle, formatUptime, shortDigest, timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
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
import { toast } from '@/components/ui/use-toast'

const NO_CREDENTIAL = 'none' // Radix Select has no empty-string value.

export function SettingsPage() {
  const qc = useQueryClient()

  const { data: status, isLoading, isError, refetch } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: Infinity,
    // Poll while an apply operation is in progress so the stage banner updates.
    refetchInterval: query => {
      const s = query.state.data
      return s?.applyStage === 'pulling' || s?.applyStage === 'restarting' ? 2000 : false
    },
  })

  const saveConfig = useMutation({
    mutationFn: (data: UpdateSelfConfigRequest) => api.system.updateConfig(data),
    onSuccess: data => {
      qc.setQueryData(['system', 'self'], data)
      toast.success('Settings saved.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to save settings.'),
  })

  const checkUpdate = useMutation({
    mutationFn: api.system.check,
    onSuccess: data => qc.setQueryData(['system', 'self'], data),
    onError: err => toast.error(err instanceof Error ? err.message : 'Update check failed.'),
  })

  const applyUpdate = useMutation({
    mutationFn: api.system.update,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['system', 'self'] }),
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to apply update.'),
  })

  return (
    <div className="mx-auto flex max-w-[720px] flex-col gap-6">
      {/* ── Title + uptime ── */}
      <header>
        <h1 className="text-2xl font-semibold tracking-[-0.02em] text-text">Settings</h1>
        {status?.startedAt ? (
          <p
            className="mt-1 inline-flex items-center gap-1.5 text-[13px] text-text-2"
            title={absoluteTitle(status.startedAt)}
          >
            <Timer className="size-3.5 shrink-0" aria-hidden />
            <span>
              Running for <span className="tnum">{formatUptime(status.startedAt)}</span>
            </span>
          </p>
        ) : (
          <p className="mt-1 text-[13px] text-text-2">Watchtower configuration and self-update.</p>
        )}
      </header>

      {isLoading ? (
        <SelfUpdateSkeleton />
      ) : isError || !status ? (
        <Banner
          tone="danger"
          title="Couldn't load settings"
          action={
            <Button size="sm" variant="secondary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          The self-update status is unavailable. Check that the Watchtower service is reachable.
        </Banner>
      ) : (
        <SelfUpdateCard
          status={status}
          onCheck={() => checkUpdate.mutate()}
          onApply={() => applyUpdate.mutate()}
          onSave={data => saveConfig.mutate(data)}
          checking={checkUpdate.isPending}
          saving={saveConfig.isPending}
          checkError={checkUpdate.error instanceof Error ? checkUpdate.error.message : null}
        />
      )}
    </div>
  )
}

// ── Self-update card ──────────────────────────────────────────────────────────

function SelfUpdateCard({
  status,
  onCheck,
  onApply,
  onSave,
  checking,
  saving,
  checkError,
}: {
  status: SelfUpdateStatus
  onCheck: () => void
  onApply: () => void
  onSave: (data: UpdateSelfConfigRequest) => void
  checking: boolean
  saving: boolean
  checkError: string | null
}) {
  const [confirmApply, setConfirmApply] = useState(false)

  const effectiveImage = status.imageName ?? status.detectedImageName
  const effectiveComposePath = status.composeFilePath ?? status.detectedComposeFilePath
  const effectiveProjectName = status.composeProjectName ?? status.detectedComposeProjectName

  const canCheck = !!effectiveImage
  const canApply = !!status.canApplyUpdate
  const applyStage = status.applyStage
  const isApplying = applyStage === 'pulling' || applyStage === 'restarting'

  return (
    <Card>
      <CardContent className="flex flex-col gap-0 p-0">
        {/* ── Header: status + check + apply ── */}
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 p-4 md:p-5">
          <UpdateStatusPill status={status} />
          {status.lastCheckedAt && (
            <span
              className="tnum text-xs text-text-2"
              title={absoluteTitle(status.lastCheckedAt)}
            >
              Checked {timeAgo(status.lastCheckedAt)}
            </span>
          )}

          <div className="ml-auto flex items-center gap-2">
            {!confirmApply && (
              <Button
                size="sm"
                variant="secondary"
                onClick={onCheck}
                loading={checking}
                disabled={checking || isApplying || !canCheck}
                title={
                  !canCheck
                    ? 'Image name unknown — ensure Watchtower is running in Docker'
                    : undefined
                }
              >
                {!checking && <RefreshCw />}
                Check
              </Button>
            )}

            {status.isOutdated && canApply && !isApplying && !confirmApply && (
              <Button
                size="sm"
                variant="primary"
                onClick={() => setConfirmApply(true)}
                className="hidden md:inline-flex"
              >
                <RotateCcw />
                Apply update
              </Button>
            )}
          </div>

          {/* Inline morph confirm (A2 — no modal, since applying restarts the app). */}
          {confirmApply && (
            <div className="flex w-full flex-col gap-2 rounded-md border border-warn-bd bg-warn-bg p-3 md:flex-row md:items-center">
              <p className="flex-1 text-sm text-text">
                Watchtower will pull the new image and restart. The UI briefly disconnects.
              </p>
              <div className="flex gap-2">
                <Button
                  size="sm"
                  variant="primary"
                  className="flex-1 md:flex-none"
                  onClick={() => {
                    setConfirmApply(false)
                    onApply()
                  }}
                >
                  Confirm
                </Button>
                <Button
                  size="sm"
                  variant="secondary"
                  className="flex-1 md:flex-none"
                  onClick={() => setConfirmApply(false)}
                >
                  Cancel
                </Button>
              </div>
            </div>
          )}
        </div>

        {/* ── Apply progress banners (live wt-live dot, per spec §4.7) ── */}
        {applyStage === 'pulling' && (
          <div className="border-t border-border px-4 pb-4 md:px-5 md:pb-5">
            <LiveBanner tone="run">Pulling latest image… this may take a moment.</LiveBanner>
          </div>
        )}
        {applyStage === 'restarting' && (
          <div className="border-t border-border px-4 pb-4 md:px-5 md:pb-5">
            <LiveBanner tone="warn">
              Restarting… Watchtower will be back in a few seconds.
            </LiveBanner>
          </div>
        )}
        {applyStage === 'error' && (
          <div className="border-t border-border px-4 pb-4 md:px-5 md:pb-5">
            <Banner tone="danger" title="Update failed">
              {status.applyError ?? 'Unknown error.'}
            </Banner>
          </div>
        )}

        {/* ── Digest rows ── */}
        {status.lastCheckedAt && status.latestImageId && (
          <div className="border-t border-border px-4 py-4 md:px-5">
            <div className="overflow-x-auto">
              <dl className="grid min-w-[18rem] grid-cols-[5rem_1fr] gap-x-3 gap-y-1.5 text-xs">
                <dt className="self-center text-text-3">Running</dt>
                <dd className="truncate font-mono text-text-2" title={status.currentImageId ?? ''}>
                  {status.currentImageId ? (
                    shortDigest(status.currentImageId)
                  ) : (
                    <span className="font-sans italic text-text-3">unknown</span>
                  )}
                </dd>
                <dt className="self-center text-text-3">Latest</dt>
                <dd
                  className={
                    status.isOutdated
                      ? 'truncate font-mono font-medium text-brand'
                      : 'truncate font-mono text-text-2'
                  }
                  title={status.latestImageId}
                >
                  {shortDigest(status.latestImageId)}
                </dd>
              </dl>
            </div>
          </div>
        )}

        {/* ── Auto-detected meta rows ── */}
        {status.isRunningInContainer && (
          <div className="border-t border-border px-4 py-4 md:px-5">
            <div className="overflow-x-auto">
              <dl className="grid min-w-[18rem] grid-cols-[5rem_1fr] gap-x-3 gap-y-1.5 text-xs">
                <dt className="self-center text-text-3">Image</dt>
                <dd className="truncate font-mono text-text-2" title={effectiveImage ?? ''}>
                  {effectiveImage ?? <span className="font-sans italic text-text-3">unknown</span>}
                  {status.imageName && <OverrideTag />}
                </dd>

                <dt className="self-center text-text-3">Compose</dt>
                <dd className="truncate font-mono text-text-2" title={effectiveComposePath ?? ''}>
                  {effectiveComposePath ?? (
                    <span className="font-sans italic text-text-3">not started via Compose</span>
                  )}
                  {status.composeFilePath && <OverrideTag />}
                </dd>

                <dt className="self-center text-text-3">Project</dt>
                <dd className="truncate font-mono text-text-2" title={effectiveProjectName ?? ''}>
                  {effectiveProjectName ?? (
                    <span className="font-sans italic text-text-3">—</span>
                  )}
                  {status.composeProjectName && <OverrideTag />}
                </dd>
              </dl>
            </div>
          </div>
        )}

        {/* ── Credential ── */}
        <CredentialRow status={status} onSave={onSave} saving={saving} />

        {/* ── Overrides (collapsible) ── */}
        <OverridesSection status={status} onSave={onSave} saving={saving} />

        {/* ── Check error ── */}
        {checkError && (
          <div className="border-t border-border px-4 py-4 md:px-5">
            <Banner tone="danger" title="Update check failed">
              {checkError}
            </Banner>
          </div>
        )}

        {/* ── Outdated but no compose info ── */}
        {status.isOutdated && !canApply && (
          <div className="border-t border-border px-4 py-4 md:px-5">
            <Banner tone="warn" title="Update available, but automatic apply isn't possible">
              Compose info is missing. Restart Watchtower manually with{' '}
              <code className="rounded bg-warn-bg px-1.5 py-0.5 font-mono text-[12px] text-warn">
                docker compose up -d
              </code>
              .
            </Banner>
          </div>
        )}
      </CardContent>

      {/* ── Mobile: full-width sticky apply action ── */}
      {status.isOutdated && canApply && !isApplying && !confirmApply && (
        <div className="sticky bottom-[calc(var(--bottombar-h)+env(safe-area-inset-bottom))] z-10 border-t border-border bg-surface p-4 md:hidden">
          <Button variant="primary" className="w-full" onClick={() => setConfirmApply(true)}>
            <RotateCcw />
            Apply update
          </Button>
        </div>
      )}
    </Card>
  )
}

// ── Status pill (StatusBadge-style, self-update vocabulary) ────────────────────

function UpdateStatusPill({ status }: { status: SelfUpdateStatus }) {
  if (!status.lastCheckedAt) {
    return <span className="text-[15px] font-medium text-text">Watchtower</span>
  }
  if (status.isOutdated) {
    return (
      <Badge tone="warn">
        <AlertTriangle className="size-3.5" aria-hidden />
        Update available
      </Badge>
    )
  }
  return (
    <Badge tone="ok">
      <CheckCircle2 className="size-3.5" aria-hidden />
      Up to date
    </Badge>
  )
}

/** Toned callout whose leading indicator is the single allowed `wt-live` dot (A6). */
function LiveBanner({ tone, children }: { tone: 'run' | 'warn'; children: React.ReactNode }) {
  const wrap =
    tone === 'run' ? 'bg-run-bg border-run-bd text-run' : 'bg-warn-bg border-warn-bd text-warn'
  return (
    <div
      role="status"
      aria-live="polite"
      className={`flex items-center gap-3 rounded-lg border p-3 text-sm ${wrap}`}
    >
      <span
        className="size-2 shrink-0 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]"
        aria-hidden
      />
      <span className="text-text">{children}</span>
    </div>
  )
}

function OverrideTag() {
  return <span className="ml-1.5 font-sans not-italic text-text-3">(override)</span>
}

// ── Credential row ────────────────────────────────────────────────────────────

function CredentialRow({
  status,
  onSave,
  saving,
}: {
  status: SelfUpdateStatus
  onSave: (data: UpdateSelfConfigRequest) => void
  saving: boolean
}) {
  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const initial = status.credentialId != null ? String(status.credentialId) : NO_CREDENTIAL
  const [value, setValue] = useState(initial)
  const dirty = value !== initial

  function handleSave() {
    onSave({
      imageName: status.imageName ?? null,
      credentialId: value === NO_CREDENTIAL ? null : Number(value),
      composeFilePath: status.composeFilePath ?? null,
      composeProjectName: status.composeProjectName ?? null,
    })
  }

  return (
    <div className="flex flex-col gap-2 border-t border-border px-4 py-4 md:flex-row md:items-end md:gap-3 md:px-5">
      <Field
        label="Credential"
        hint="Only needed to pull the Watchtower image from a private registry."
        className="flex-1"
      >
        {({ id }) => (
          <Select value={value} onValueChange={setValue}>
            <SelectTrigger id={id}>
              <SelectValue placeholder="None (public image)" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={NO_CREDENTIAL}>None (public image)</SelectItem>
              {credentials.map(c => (
                <SelectItem key={c.id} value={String(c.id)}>
                  {c.name} ({c.username})
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </Field>
      {dirty && (
        <Button
          variant="primary"
          size="md"
          onClick={handleSave}
          loading={saving}
          className="w-full md:w-auto"
        >
          Save
        </Button>
      )}
    </div>
  )
}

// ── Overrides (collapsible) ───────────────────────────────────────────────────

function OverridesSection({
  status,
  onSave,
  saving,
}: {
  status: SelfUpdateStatus
  onSave: (data: UpdateSelfConfigRequest) => void
  saving: boolean
}) {
  const hasOverrides = !!(status.imageName || status.composeFilePath || status.composeProjectName)
  const [open, setOpen] = useState(hasOverrides)

  const [imageName, setImageName] = useState(status.imageName ?? '')
  const [composeFilePath, setComposeFilePath] = useState(status.composeFilePath ?? '')
  const [composeProjectName, setComposeProjectName] = useState(status.composeProjectName ?? '')

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    onSave({
      imageName: imageName.trim() || null,
      credentialId: status.credentialId ?? null,
      composeFilePath: composeFilePath.trim() || null,
      composeProjectName: composeProjectName.trim() || null,
    })
  }

  return (
    <div className="border-t border-border">
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        aria-expanded={open}
        className="flex w-full items-center gap-1.5 px-4 py-3 text-[13px] text-text-2 transition-colors hover:bg-surface-2 hover:text-text md:px-5"
      >
        {open ? (
          <ChevronDown className="size-4 shrink-0" aria-hidden />
        ) : (
          <ChevronRight className="size-4 shrink-0" aria-hidden />
        )}
        {hasOverrides ? 'Overrides active' : 'Override auto-detected settings'}
      </button>

      {open && (
        <form
          onSubmit={handleSubmit}
          className="flex flex-col gap-4 border-t border-border bg-surface-2/40 px-4 py-4 md:px-5"
        >
          <p className="text-[13px] text-text-2">
            Leave a field blank to use the auto-detected value. Set an override to force a specific
            value.
          </p>
          <Field
            label="Image name"
            hint="e.g. ghcr.io/owner/watchtower:latest. Defaults to the detected image."
          >
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                mono
                placeholder={status.detectedImageName ?? 'ghcr.io/owner/watchtower:latest'}
                value={imageName}
                onChange={e => setImageName(e.target.value)}
              />
            )}
          </Field>
          <Field label="Compose file path" hint="Absolute path on the host, e.g. /opt/watchtower/docker-compose.yml.">
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                mono
                placeholder={status.detectedComposeFilePath ?? '/opt/watchtower/docker-compose.yml'}
                value={composeFilePath}
                onChange={e => setComposeFilePath(e.target.value)}
              />
            )}
          </Field>
          <Field label="Project name" hint="Compose project name. Defaults to the detected project.">
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                mono
                placeholder={status.detectedComposeProjectName ?? 'watchtower'}
                value={composeProjectName}
                onChange={e => setComposeProjectName(e.target.value)}
              />
            )}
          </Field>
          <div className="flex justify-end">
            <Button type="submit" variant="primary" loading={saving} className="w-full md:w-auto">
              Save overrides
            </Button>
          </div>
        </form>
      )}
    </div>
  )
}

// ── Loading skeleton (matches the card shape) ─────────────────────────────────

function SelfUpdateSkeleton() {
  return (
    <Card>
      <CardContent className="flex flex-col gap-0 p-0">
        <div className="flex items-center gap-3 p-4 md:p-5">
          <Skeleton className="h-6 w-32 rounded-full" />
          <Skeleton className="h-4 w-24" />
          <Skeleton className="ml-auto h-8 w-20" />
        </div>
        <div className="flex flex-col gap-2 border-t border-border px-4 py-4 md:px-5">
          <Skeleton variant="line" className="w-3/4" />
          <Skeleton variant="line" className="w-2/3" />
        </div>
        <div className="flex flex-col gap-2 border-t border-border px-4 py-4 md:px-5">
          <Skeleton variant="line" className="w-2/3" />
          <Skeleton variant="line" className="w-1/2" />
        </div>
        <div className="border-t border-border px-4 py-4 md:px-5">
          <Skeleton className="h-9 w-full" />
        </div>
      </CardContent>
    </Card>
  )
}
