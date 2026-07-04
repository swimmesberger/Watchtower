import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  AlertTriangle,
  CheckCircle,
  ChevronDown,
  ChevronRight,
  Clock,
  Loader2,
  RefreshCw,
  RotateCcw,
  Timer,
} from 'lucide-react'
import { api } from '@/lib/api'
import type { SelfUpdateStatus, UpdateSelfConfigRequest } from '@/lib/types'

export function SettingsPage() {
  const qc = useQueryClient()

  const { data: status, isLoading } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: Infinity,
  })

  // Poll while an apply operation is in progress so the stage banner updates.
  const isApplyingInProgress = status?.applyStage === 'pulling' || status?.applyStage === 'restarting'
  useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    refetchInterval: isApplyingInProgress ? 2000 : false,
    enabled: isApplyingInProgress,
  })

  const saveConfig = useMutation({
    mutationFn: (data: UpdateSelfConfigRequest) => api.system.updateConfig(data),
    onSuccess: data => qc.setQueryData(['system', 'self'], data),
  })

  const checkUpdate = useMutation({
    mutationFn: api.system.check,
    onSuccess: data => qc.setQueryData(['system', 'self'], data),
  })

  const applyUpdate = useMutation({
    mutationFn: api.system.update,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['system', 'self'] }),
  })

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-full text-[var(--text-secondary)]">
        <Loader2 className="size-5 animate-spin mr-2" /> Loading…
      </div>
    )
  }

  return (
    <div className="p-6 space-y-6 max-w-[640px]">
      <div>
        <h1 className="text-[22px] font-bold tracking-tight">Settings</h1>
        <p className="text-[13px] text-[var(--text-tertiary)] mt-0.5">Watchtower configuration and self-update</p>
        {status?.startedAt && (
          <p className="inline-flex items-center gap-1.5 text-[12px] text-[var(--text-tertiary)] mt-1.5">
            <Timer className="size-3.5 shrink-0" />
            Running for {formatUptime(status.startedAt)}
          </p>
        )}
      </div>

      <section className="space-y-2">
        <h2 className="text-sm font-semibold">Self-Update</h2>
        <SelfUpdateCard
          status={status ?? null}
          onCheck={() => checkUpdate.mutate()}
          onApply={() => applyUpdate.mutate()}
          onSave={data => saveConfig.mutate(data)}
          checking={checkUpdate.isPending}
          saving={saveConfig.isPending}
          checkError={checkUpdate.error?.message ?? null}
        />
      </section>
    </div>
  )
}

// ── Main self-update card ─────────────────────────────────────────────────────

function SelfUpdateCard({
  status,
  onCheck,
  onApply,
  onSave,
  checking,
  saving,
  checkError,
}: {
  status: SelfUpdateStatus | null
  onCheck: () => void
  onApply: () => void
  onSave: (data: UpdateSelfConfigRequest) => void
  checking: boolean
  saving: boolean
  checkError: string | null
}) {
  const [showOverrides, setShowOverrides] = useState(false)
  const [confirmApply, setConfirmApply] = useState(false)

  const effectiveImage = status?.imageName ?? status?.detectedImageName
  const effectiveComposePath = status?.composeFilePath ?? status?.detectedComposeFilePath
  const effectiveProjectName = status?.composeProjectName ?? status?.detectedComposeProjectName

  const canCheck = !!effectiveImage
  const canApply = !!status?.canApplyUpdate
  const applyStage = status?.applyStage ?? 'idle'
  const isApplying = applyStage === 'pulling' || applyStage === 'restarting'

  // Show overrides section automatically if any override is already set.
  const hasOverrides = !!(status?.imageName || status?.composeFilePath || status?.composeProjectName)

  return (
    <div className="rounded-[14px] border border-[rgba(255,255,255,0.06)] bg-[var(--card)] divide-y divide-[rgba(255,255,255,0.06)] overflow-hidden">

      {/* ── Header: status + actions ── */}
      <div className="flex items-center gap-3 px-4 py-3">
        <StatusBadge status={status} />
        <span className="text-xs text-[var(--text-tertiary)] ml-auto">
          {status?.lastCheckedAt
            ? <>Checked {new Date(status.lastCheckedAt).toLocaleString()}</>
            : 'Not yet checked'}
        </span>
        <button
          onClick={onCheck}
          disabled={checking || isApplying || !canCheck}
          title={!canCheck ? 'Image name unknown — ensure Watchtower is running in Docker' : undefined}
          className="inline-flex items-center gap-1.5 rounded-[10px] border border-[var(--border)] px-3 py-1.5 text-xs font-medium text-[var(--text-secondary)] hover:bg-[var(--accent)] hover:text-[var(--text-primary)] transition-all disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {checking ? <Loader2 className="size-3.5 animate-spin" /> : <RefreshCw className="size-3.5" />}
          Check
        </button>
        {status?.isOutdated && canApply && !confirmApply && !isApplying && (
          <button
            onClick={() => setConfirmApply(true)}
            className="inline-flex items-center gap-1.5 rounded-[10px] bg-gradient-to-br from-[var(--warning)] to-[#d97706] text-[var(--primary-foreground)] px-3 py-1.5 text-xs font-semibold hover:shadow-[0_0_12px_rgba(245,158,11,0.3)] transition-all"
          >
            <RotateCcw className="size-3.5" />
            Apply Update
          </button>
        )}
        {confirmApply && (
          <>
            <span className="text-xs font-medium text-[var(--warning)]">Watchtower will restart.</span>
            <button
              onClick={() => { setConfirmApply(false); onApply() }}
              className="rounded-[10px] bg-gradient-to-br from-[var(--warning)] to-[#d97706] text-[var(--primary-foreground)] px-3 py-1.5 text-xs font-semibold hover:shadow-[0_0_12px_rgba(245,158,11,0.3)] transition-all"
            >Confirm</button>
            <button
              onClick={() => setConfirmApply(false)}
              className="rounded-[10px] border border-[var(--border)] px-3 py-1.5 text-xs text-[var(--text-secondary)] hover:bg-[var(--accent)] hover:text-[var(--text-primary)] transition-all"
            >Cancel</button>
          </>
        )}
      </div>

      {/* ── Apply progress banner ── */}
      {applyStage === 'pulling' && (
        <div className="flex items-center gap-2 px-4 py-2.5 text-xs bg-[var(--running-bg)] text-[var(--running)] border-b border-[var(--running-border)]">
          <Loader2 className="size-3.5 animate-spin shrink-0" />
          Pulling latest image… this may take a moment.
        </div>
      )}
      {applyStage === 'restarting' && (
        <div className="flex items-center gap-2 px-4 py-2.5 text-xs bg-[var(--warning-bg)] text-[var(--warning)] border-b border-[var(--warning-border)]">
          <Loader2 className="size-3.5 animate-spin shrink-0" />
          Restarting container… Watchtower will be back in a few seconds.
        </div>
      )}
      {applyStage === 'error' && (
        <div className="flex items-start gap-2 px-4 py-2.5 text-xs bg-[var(--danger-bg)] text-[var(--danger)] border-b border-[var(--danger-border)]">
          <AlertTriangle className="size-3.5 shrink-0 mt-0.5" />
          <span><strong>Update failed:</strong> {status?.applyError ?? 'Unknown error'}</span>
        </div>
      )}

      {/* ── Digest row — only shown when both digests are comparable manifest digests ── */}
      {status?.lastCheckedAt && status.latestImageId && (
        <div className="grid grid-cols-[5rem_1fr] gap-x-3 gap-y-1 px-4 py-3 font-[var(--font-mono)] text-xs border-b border-[rgba(255,255,255,0.06)]">
          <span className="text-[var(--text-tertiary)] self-center">Running</span>
          <span className="truncate text-[var(--text-secondary)]" title={status.currentImageId ?? ''}>
            {status.currentImageId ? shortDigest(status.currentImageId) : <span className="italic font-sans text-[var(--text-tertiary)]">unknown</span>}
          </span>
          <span className="text-[var(--text-tertiary)] self-center">Latest</span>
          <span
            className={`truncate ${status.isOutdated ? 'text-[var(--warning)] font-medium' : 'text-[var(--text-secondary)]'}`}
            title={status.latestImageId}
          >
            {shortDigest(status.latestImageId)}
          </span>
        </div>
      )}

      {/* ── Auto-detected info ── */}
      {status?.isRunningInContainer && (
        <div className="grid grid-cols-[5rem_1fr] gap-x-3 gap-y-1 px-4 py-3 text-xs border-b border-[rgba(255,255,255,0.06)]">
          <span className="text-[var(--text-tertiary)] self-center w-20">Image</span>
          <span className="font-[var(--font-mono)] text-[11px] text-[var(--text-secondary)] truncate" title={effectiveImage ?? ''}>
            {effectiveImage ?? <span className="italic text-[var(--text-tertiary)]">unknown</span>}
            {status.imageName && <span className="ml-1.5 text-[var(--text-tertiary)] font-sans not-italic">(override)</span>}
          </span>
          <span className="text-[var(--text-tertiary)] self-center w-20">Compose</span>
          <span className="font-[var(--font-mono)] text-[11px] text-[var(--text-secondary)] truncate" title={effectiveComposePath ?? ''}>
            {effectiveComposePath
              ? <>{effectiveComposePath}<span className="text-[var(--text-tertiary)] font-sans"> · {effectiveProjectName}</span></>
              : <span className="italic text-[var(--text-tertiary)]">not started via Compose</span>}
            {status.composeFilePath && <span className="ml-1.5 text-[var(--text-tertiary)] font-sans not-italic">(override)</span>}
          </span>
        </div>
      )}

      {/* ── Credential row (always visible) ── */}
      <CredentialRow
        status={status}
        onSave={onSave}
        saving={saving}
      />

      {/* ── Override toggle ── */}
      <button
        type="button"
        onClick={() => setShowOverrides(v => !v)}
        className="flex items-center gap-1.5 w-full px-4 py-2.5 text-xs text-[var(--text-tertiary)] hover:text-[var(--text-primary)] hover:bg-[var(--accent)] transition-colors"
      >
        {showOverrides || hasOverrides
          ? <ChevronDown className="size-3.5" />
          : <ChevronRight className="size-3.5" />}
        {hasOverrides ? 'Overrides active' : 'Override auto-detected settings'}
      </button>

      {(showOverrides || hasOverrides) && (
        <OverridesForm status={status} onSave={onSave} saving={saving} />
      )}

      {/* ── Errors ── */}
      {checkError && (
        <div className="px-4 py-2.5 text-xs">
          <p className="text-[var(--danger)]" role="alert">{checkError}</p>
        </div>
      )}

      {/* ── Outdated but no compose ── */}
      {status?.isOutdated && !canApply && (
        <div className="flex items-center gap-2 px-4 py-2.5 text-xs text-[var(--warning)] bg-[var(--warning-bg)] border-b border-[var(--warning-border)]">
          <Clock className="size-3.5 shrink-0" />
          Update available but Compose info is missing — restart manually with{' '}
          <code className="font-[var(--font-mono)] bg-[rgba(245,158,11,0.08)] px-1.5 rounded">docker compose up -d</code>
        </div>
      )}
    </div>
  )
}

// ── Status badge ──────────────────────────────────────────────────────────────

function StatusBadge({ status }: { status: SelfUpdateStatus | null }) {
  if (!status?.lastCheckedAt) {
    return <span className="text-sm font-medium text-[var(--text-secondary)]">Watchtower</span>
  }
  if (status.isOutdated) {
    return (
      <span className="inline-flex items-center gap-1.5 text-sm font-semibold text-[var(--warning)]">
        <AlertTriangle className="size-4 shrink-0" />
        Update available
      </span>
    )
  }
  return (
    <span className="inline-flex items-center gap-1.5 text-sm font-semibold text-[var(--success)]">
      <CheckCircle className="size-4 shrink-0" />
      Up to date
    </span>
  )
}

// ── Credential row ────────────────────────────────────────────────────────────

function CredentialRow({
  status,
  onSave,
  saving,
}: {
  status: SelfUpdateStatus | null
  onSave: (data: UpdateSelfConfigRequest) => void
  saving: boolean
}) {
  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })
  const [credentialId, setCredentialId] = useState<string>(
    status?.credentialId != null ? String(status.credentialId) : '',
  )
  const [dirty, setDirty] = useState(false)

  function handleSave() {
    onSave({
      imageName: status?.imageName ?? null,
      credentialId: credentialId ? Number(credentialId) : null,
      composeFilePath: status?.composeFilePath ?? null,
      composeProjectName: status?.composeProjectName ?? null,
    })
    setDirty(false)
  }

  return (
    <div className="flex items-center gap-3 px-4 py-3">
      <label htmlFor="self-credential" className="text-xs font-medium shrink-0 w-20 text-[var(--text-tertiary)]">
        Credential
      </label>
      <select
        id="self-credential"
        value={credentialId}
        onChange={e => { setCredentialId(e.target.value); setDirty(true) }}
        className="input flex-1 min-w-0 text-xs"
      >
        <option value="">None (public image)</option>
        {credentials.map(c => (
          <option key={c.id} value={c.id}>{c.name} ({c.username})</option>
        ))}
      </select>
      {dirty && (
        <button
          type="button"
          onClick={handleSave}
          disabled={saving}
          className="inline-flex items-center gap-1 rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-3 py-1.5 text-xs font-semibold hover:shadow-[0_0_12px_rgba(20,184,166,0.3)] transition-all disabled:opacity-50"
        >
          {saving && <Loader2 className="size-3 animate-spin" />}
          Save
        </button>
      )}
    </div>
  )
}

// ── Override fields (collapsed by default) ────────────────────────────────────

function OverridesForm({
  status,
  onSave,
  saving,
}: {
  status: SelfUpdateStatus | null
  onSave: (data: UpdateSelfConfigRequest) => void
  saving: boolean
}) {
  const [imageName, setImageName] = useState(status?.imageName ?? '')
  const [composeFilePath, setComposeFilePath] = useState(status?.composeFilePath ?? '')
  const [composeProjectName, setComposeProjectName] = useState(status?.composeProjectName ?? '')

  function handleSave(e: React.FormEvent) {
    e.preventDefault()
    onSave({
      imageName: imageName || null,
      credentialId: status?.credentialId ?? null,
      composeFilePath: composeFilePath || null,
      composeProjectName: composeProjectName || null,
    })
  }

  return (
    <form onSubmit={handleSave} className="px-4 py-4 space-y-3 bg-[rgba(255,255,255,0.02)]">
      <p className="text-xs text-[var(--text-tertiary)]">
        Leave blank to use auto-detected values. Set an override to force a specific value.
      </p>
      <div className="grid sm:grid-cols-[1fr_1fr_1fr] gap-3">
        <div className="space-y-1">
          <label htmlFor="ov-image" className="text-xs font-semibold text-[var(--text-secondary)]">Image Name</label>
          <input
            id="ov-image"
            type="text"
            placeholder={status?.detectedImageName ?? 'ghcr.io/owner/watchtower:latest'}
            value={imageName}
            onChange={e => setImageName(e.target.value)}
            className="input w-full font-[var(--font-mono)] text-[11px]"
          />
        </div>
        <div className="space-y-1">
          <label htmlFor="ov-compose" className="text-xs font-semibold text-[var(--text-secondary)]">Compose File Path</label>
          <input
            id="ov-compose"
            type="text"
            placeholder={status?.detectedComposeFilePath ?? '/opt/watchtower/docker-compose.yml'}
            value={composeFilePath}
            onChange={e => setComposeFilePath(e.target.value)}
            className="input w-full font-[var(--font-mono)] text-[11px]"
          />
        </div>
        <div className="space-y-1">
          <label htmlFor="ov-project" className="text-xs font-semibold text-[var(--text-secondary)]">Project Name</label>
          <input
            id="ov-project"
            type="text"
            placeholder={status?.detectedComposeProjectName ?? 'watchtower'}
            value={composeProjectName}
            onChange={e => setComposeProjectName(e.target.value)}
            className="input w-full font-[var(--font-mono)] text-[11px]"
          />
        </div>
      </div>
      <div className="flex justify-end">
        <button
          type="submit"
          disabled={saving}
          className="inline-flex items-center gap-1.5 rounded-[10px] bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] text-[var(--primary-foreground)] px-3 py-1.5 text-xs font-semibold hover:shadow-[0_0_12px_rgba(20,184,166,0.3)] transition-all disabled:opacity-50"
        >
          {saving && <Loader2 className="size-3 animate-spin" />}
          Save overrides
        </button>
      </div>
    </form>
  )
}

/** Trims a "sha256:abc123…" digest to a short readable form. */
function shortDigest(digest: string | null): string {
  if (!digest) return '—'
  const prefix = 'sha256:'
  return digest.startsWith(prefix) ? digest.slice(0, prefix.length + 12) + '…' : digest.slice(0, 19) + '…'
}

/** Formats the duration since a given ISO timestamp as a human-readable uptime string. */
function formatUptime(startedAt: string): string {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000))
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)

  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${minutes}m`
  if (minutes > 0) return `${minutes}m`
  return `${seconds}s`
}
