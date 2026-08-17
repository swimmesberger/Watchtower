import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  AlertTriangle,
  CheckCircle2,
  Lock,
  RefreshCw,
  RotateCcw,
  Timer,
} from 'lucide-react'
import { api } from '@/lib/api'
import type {
  AuthConfig,
  AutomationConfig,
  MetricsBackend,
  MetricsConfig,
  ProxyConfig,
  SelfUpdateStatus,
  UpdateSelfConfigRequest,
} from '@/lib/types'
import { absoluteTitle, formatUptime, shortDigest, timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { SecretField } from '@/components/ui/secret-field'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'

const NO_CREDENTIAL = 'none' // Radix Select has no empty-string value.

// ── Env-pinned settings ───────────────────────────────────────────────────────
// Environment variables win over runtime settings (infrastructure-as-code pins). The backend reports
// which config paths are pinned; those fields render disabled with the variable named, instead of
// accepting an edit that would silently never take effect.

const envVarName = (path: string) => path.replace(/:/g, '__').toUpperCase()

/** Small lock note naming the env var that pins a field. Render next to the disabled control. */
function PinnedNote({ path }: { path: string }) {
  return (
    <span
      className="inline-flex items-center gap-1 text-[11px] text-text-3"
      title="Set via environment variable — remove it from the deployment (and restart) to edit here."
    >
      <Lock className="size-3 shrink-0" aria-hidden />
      <span className="font-mono">{envVarName(path)}</span>
    </span>
  )
}

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

      <AutomationCard />

      <MetricsCard />

      <ProxyCard />

      <AuthCard />
    </div>
  )
}

// ── Automation card (runtime-editable background checks) ────────────────────────

function AutomationCard() {
  const qc = useQueryClient()
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['system', 'automation'],
    queryFn: api.system.getAutomation,
    staleTime: 60_000,
  })

  const [draft, setDraft] = useState<AutomationConfig | null>(null)
  // Seed the local form once the query resolves.
  const form = draft ?? data ?? null
  const dirty = draft != null && data != null && JSON.stringify(draft) !== JSON.stringify(data)

  const save = useMutation({
    mutationFn: (next: AutomationConfig) => api.system.updateAutomation(next),
    onSuccess: next => {
      qc.setQueryData(['system', 'automation'], next)
      setDraft(null)
      toast.success('Automation settings saved.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to save.'),
  })

  function set<K extends keyof AutomationConfig>(key: K, value: AutomationConfig[K]) {
    if (!form) return
    setDraft({ ...form, [key]: value })
  }

  const pinnedPath = (path: string) => (data?.pinnedPaths.includes(path) ? path : null)

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text">Automation</h2>
        <p className="mt-0.5 text-[13px] text-text-2">
          Periodic background checks. Changes apply immediately — no restart needed.
        </p>
      </div>

      {isLoading || !form ? (
        <Card>
          <CardContent className="flex flex-col gap-4 p-5">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : isError ? (
        <Banner
          tone="danger"
          title="Couldn't load automation settings"
          action={
            <Button size="sm" variant="secondary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          The background-check configuration is unavailable.
        </Banner>
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-5 p-5">
            <ToggleRow
              label="Check for Watchtower updates"
              hint="Periodically compares the running image with its registry so the update badge stays fresh."
              enabled={form.autoCheckEnabled}
              minutes={form.autoCheckIntervalMinutes}
              onToggle={v => set('autoCheckEnabled', v)}
              onMinutes={v => set('autoCheckIntervalMinutes', v)}
              pinnedToggle={pinnedPath('Watchtower:AutoCheckEnabled')}
              pinnedMinutes={pinnedPath('Watchtower:AutoCheckIntervalMinutes')}
            />
            <div className="h-px bg-border" />
            <ToggleRow
              label="Check stacks for image updates"
              hint="Periodically checks each stack's images for newer versions in their registries."
              enabled={form.stackCheckEnabled}
              minutes={form.stackCheckIntervalMinutes}
              onToggle={v => set('stackCheckEnabled', v)}
              onMinutes={v => set('stackCheckIntervalMinutes', v)}
              pinnedToggle={pinnedPath('Watchtower:StackCheckEnabled')}
              pinnedMinutes={pinnedPath('Watchtower:StackCheckIntervalMinutes')}
            />
            <div className="h-px bg-border" />
            <ToggleRow
              label="Prune dangling images"
              hint="Periodically removes untagged (dangling) image layers left behind by pulls, reclaiming disk. Tagged images are never touched."
              enabled={form.imagePruneEnabled}
              minutes={form.imagePruneIntervalMinutes}
              onToggle={v => set('imagePruneEnabled', v)}
              onMinutes={v => set('imagePruneIntervalMinutes', v)}
              pinnedToggle={pinnedPath('Watchtower:ImagePruneEnabled')}
              pinnedMinutes={pinnedPath('Watchtower:ImagePruneIntervalMinutes')}
            />
            <div className="flex items-center gap-3">
              <Button
                variant="primary"
                size="sm"
                disabled={!dirty || save.isPending}
                loading={save.isPending}
                onClick={() => draft && save.mutate(draft)}
              >
                Save automation
              </Button>
              {dirty && <span className="text-[13px] text-text-2">Unsaved changes</span>}
            </div>
          </CardContent>
        </Card>
      )}
    </section>
  )
}

function ToggleRow({
  label,
  hint,
  enabled,
  minutes,
  onToggle,
  onMinutes,
  pinnedToggle = null,
  pinnedMinutes = null,
}: {
  label: string
  hint: string
  enabled: boolean
  minutes: number
  onToggle: (v: boolean) => void
  onMinutes: (v: number) => void
  /** Config path when the toggle is env-pinned (renders it disabled with the variable named). */
  pinnedToggle?: string | null
  /** Config path when the interval is env-pinned. */
  pinnedMinutes?: string | null
}) {
  return (
    <div className="flex flex-col gap-3">
      <label className="flex items-start justify-between gap-4">
        <span className="min-w-0">
          <span className="block text-[13px] font-medium text-text">{label}</span>
          <span className="mt-0.5 block text-[13px] text-text-2">{hint}</span>
          {pinnedToggle && (
            <span className="mt-1 block">
              <PinnedNote path={pinnedToggle} />
            </span>
          )}
        </span>
        <Switch
          checked={enabled}
          onCheckedChange={onToggle}
          disabled={pinnedToggle != null}
          aria-label={label}
        />
      </label>
      {enabled && (
        <div className="flex items-center gap-2 pl-0.5">
          <span className="text-[13px] text-text-2">Every</span>
          <Input
            type="number"
            min={1}
            max={1440}
            value={minutes}
            onChange={e => onMinutes(Math.max(1, Math.min(1440, Number(e.target.value) || 1)))}
            className="w-20 tnum"
            disabled={pinnedMinutes != null}
            aria-label={`${label} interval in minutes`}
          />
          <span className="text-[13px] text-text-2">minutes</span>
          {pinnedMinutes && <PinnedNote path={pinnedMinutes} />}
        </div>
      )}
    </div>
  )
}

// ── Metrics backend card (ADR-0013, runtime-switchable) ───────────────────────

const BACKEND_LABELS: Record<MetricsBackend, string> = {
  sqlite: 'Persisted (SQLite, default)',
  memory: 'Live only (in-memory)',
  influxdb: 'External InfluxDB (bring your own)',
}

interface MetricsDraft {
  backend: MetricsBackend
  retentionDays: number
  influxUrl: string
  influxOrg: string
  influxBucket: string
  /** Only sent when non-empty — an empty field keeps the stored token. */
  influxToken: string
  influxComposeProjectTag: string
  influxDiskMountpoint: string
}

function toDraft(config: MetricsConfig): MetricsDraft {
  return {
    backend: config.backend,
    retentionDays: config.retentionDays,
    influxUrl: config.influx.url ?? '',
    influxOrg: config.influx.org ?? '',
    influxBucket: config.influx.bucket ?? '',
    influxToken: '',
    influxComposeProjectTag: config.influx.composeProjectTag,
    influxDiskMountpoint: config.influx.diskMountpoint,
  }
}

function MetricsCard() {
  const qc = useQueryClient()
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['metrics', 'config'],
    queryFn: api.metrics.getConfig,
    staleTime: 60_000,
  })

  const [draft, setDraft] = useState<MetricsDraft | null>(null)
  const form = draft ?? (data ? toDraft(data) : null)
  const dirty = draft != null && data != null && JSON.stringify(draft) !== JSON.stringify(toDraft(data))

  const save = useMutation({
    mutationFn: (next: MetricsDraft) =>
      api.metrics.updateConfig({
        backend: next.backend,
        retentionDays: next.retentionDays,
        influxUrl: next.influxUrl.trim() || null,
        influxOrg: next.influxOrg.trim() || null,
        influxBucket: next.influxBucket.trim() || null,
        influxToken: next.influxToken.trim() || null,
        influxComposeProjectTag: next.influxComposeProjectTag.trim(),
        influxDiskMountpoint: next.influxDiskMountpoint.trim() || '/',
      }),
    onSuccess: next => {
      const availabilityChanged = data != null && data.historyAvailable !== next.historyAvailable
      qc.setQueryData(['metrics', 'config'], next)
      qc.invalidateQueries({ queryKey: ['metrics'] })
      setDraft(null)
      if (availabilityChanged) {
        // The History nav item is gated on the boot capability snapshot — rebuild it.
        toast.success('Metrics backend switched — reloading…')
        setTimeout(() => window.location.reload(), 800)
      } else {
        toast.success('Metrics settings saved.')
      }
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to save.'),
  })

  function set<K extends keyof MetricsDraft>(key: K, value: MetricsDraft[K]) {
    if (!form) return
    setDraft({ ...form, [key]: value })
  }

  const isPinned = (path: string) => data?.pinnedPaths.includes(path) ?? false
  const pinnedPath = (path: string) => (isPinned(path) ? path : null)

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text">Metrics</h2>
        <p className="mt-0.5 text-[13px] text-text-2">
          Where resource metrics come from and how long history is kept. Switching applies immediately —
          no restart needed.
        </p>
      </div>

      {isLoading || !form ? (
        <Card>
          <CardContent className="flex flex-col gap-4 p-5">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : isError ? (
        <Banner
          tone="danger"
          title="Couldn't load metrics settings"
          action={
            <Button size="sm" variant="secondary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          The metrics-backend configuration is unavailable.
        </Banner>
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-5 p-5">
            <Field label="Backend" hint="Persisted keeps history in Watchtower's own database with zero dependencies.">
              {({ id }) => (
                <>
                  <Select
                    value={form.backend}
                    onValueChange={v => set('backend', v as MetricsBackend)}
                    disabled={isPinned('Watchtower:Metrics:Backend')}
                  >
                    <SelectTrigger id={id}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {(Object.keys(BACKEND_LABELS) as MetricsBackend[]).map(b => (
                        <SelectItem key={b} value={b}>
                          {BACKEND_LABELS[b]}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  {pinnedPath('Watchtower:Metrics:Backend') && (
                    <PinnedNote path="Watchtower:Metrics:Backend" />
                  )}
                </>
              )}
            </Field>

            {form.backend === 'sqlite' && (
              <div className="flex items-center gap-2 pl-0.5">
                <span className="text-[13px] text-text-2">Keep history for</span>
                <Input
                  type="number"
                  min={1}
                  max={365}
                  value={form.retentionDays}
                  onChange={e =>
                    set('retentionDays', Math.max(1, Math.min(365, Number(e.target.value) || 1)))
                  }
                  className="w-20 tnum"
                  disabled={isPinned('Watchtower:Metrics:RetentionDays')}
                  aria-label="History retention in days"
                />
                <span className="text-[13px] text-text-2">days</span>
                {pinnedPath('Watchtower:Metrics:RetentionDays') && (
                  <PinnedNote path="Watchtower:Metrics:RetentionDays" />
                )}
              </div>
            )}

            {form.backend === 'memory' && (
              <p className="text-[13px] text-text-2">
                Only the ~15-minute live window is kept, nothing is written to disk. The History view is
                hidden on this backend.
              </p>
            )}

            {form.backend === 'influxdb' && (
              <div className="flex flex-col gap-4">
                <p className="text-[13px] text-text-2">
                  Watchtower reads from an InfluxDB v2 an external collector fills (it never writes).
                  See the metrics-history doc for the expected collector schema.
                </p>
                <Field label="URL" hint="InfluxDB v2 base URL, e.g. http://influxdb:8086.">
                  {({ id }) => (
                    <>
                      <Input
                        id={id}
                        mono
                        placeholder="http://influxdb:8086"
                        value={form.influxUrl}
                        onChange={e => set('influxUrl', e.target.value)}
                        disabled={isPinned('Watchtower:Metrics:Influx:Url')}
                      />
                      {pinnedPath('Watchtower:Metrics:Influx:Url') && (
                        <PinnedNote path="Watchtower:Metrics:Influx:Url" />
                      )}
                    </>
                  )}
                </Field>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field label="Organization">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          value={form.influxOrg}
                          onChange={e => set('influxOrg', e.target.value)}
                          disabled={isPinned('Watchtower:Metrics:Influx:Org')}
                        />
                        {pinnedPath('Watchtower:Metrics:Influx:Org') && (
                          <PinnedNote path="Watchtower:Metrics:Influx:Org" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field label="Bucket">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          value={form.influxBucket}
                          onChange={e => set('influxBucket', e.target.value)}
                          disabled={isPinned('Watchtower:Metrics:Influx:Bucket')}
                        />
                        {pinnedPath('Watchtower:Metrics:Influx:Bucket') && (
                          <PinnedNote path="Watchtower:Metrics:Influx:Bucket" />
                        )}
                      </>
                    )}
                  </Field>
                </div>
                <Field
                  label="API token"
                  hint={
                    isPinned('Watchtower:Metrics:Influx:Token')
                      ? 'The token is set via the environment and cannot be changed here.'
                      : data?.influx.hasToken
                        ? 'A token is stored. Leave blank to keep it; enter a new one to replace it.'
                        : 'Token with read access to the bucket.'
                  }
                >
                  {() => (
                    <>
                      <SecretField
                        value={form.influxToken}
                        copyable={false}
                        placeholder={data?.influx.hasToken ? '••••••••  (unchanged)' : 'Paste a read token'}
                        onChange={v => set('influxToken', v)}
                        readOnly={isPinned('Watchtower:Metrics:Influx:Token')}
                        aria-label="InfluxDB API token"
                      />
                      {pinnedPath('Watchtower:Metrics:Influx:Token') && (
                        <PinnedNote path="Watchtower:Metrics:Influx:Token" />
                      )}
                    </>
                  )}
                </Field>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field
                    label="Compose-project tag"
                    hint="Tag carrying the compose project (per-stack rollup). Leave empty unless the collector emits it."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="compose_project"
                          value={form.influxComposeProjectTag}
                          onChange={e => set('influxComposeProjectTag', e.target.value)}
                          disabled={isPinned('Watchtower:Metrics:Influx:ComposeProjectTag')}
                        />
                        {pinnedPath('Watchtower:Metrics:Influx:ComposeProjectTag') && (
                          <PinnedNote path="Watchtower:Metrics:Influx:ComposeProjectTag" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field label="Disk mount point" hint="Mount point reported for the host-disk cell.">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="/"
                          value={form.influxDiskMountpoint}
                          onChange={e => set('influxDiskMountpoint', e.target.value)}
                          disabled={isPinned('Watchtower:Metrics:Influx:DiskMountpoint')}
                        />
                        {pinnedPath('Watchtower:Metrics:Influx:DiskMountpoint') && (
                          <PinnedNote path="Watchtower:Metrics:Influx:DiskMountpoint" />
                        )}
                      </>
                    )}
                  </Field>
                </div>
              </div>
            )}

            <div className="flex items-center gap-3">
              <Button
                variant="primary"
                size="sm"
                disabled={!dirty || save.isPending}
                loading={save.isPending}
                onClick={() => draft && save.mutate(draft)}
              >
                Save metrics
              </Button>
              {dirty && <span className="text-[13px] text-text-2">Unsaved changes</span>}
            </div>
          </CardContent>
        </Card>
      )}
    </section>
  )
}

// ── Reverse proxy card (runtime-switchable, no restart) ───────────────────────

interface ProxyDraft {
  enabled: boolean
  adminEmail: string
  caddyImage: string
}

function toProxyDraft(config: ProxyConfig): ProxyDraft {
  return {
    enabled: config.enabled,
    adminEmail: config.adminEmail ?? '',
    caddyImage: config.caddyImage,
  }
}

function ProxyCard() {
  const qc = useQueryClient()
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['proxy', 'config'],
    queryFn: api.proxy.getConfig,
    staleTime: 60_000,
  })

  const [draft, setDraft] = useState<ProxyDraft | null>(null)
  const form = draft ?? (data ? toProxyDraft(data) : null)
  const dirty = draft != null && data != null && JSON.stringify(draft) !== JSON.stringify(toProxyDraft(data))

  const save = useMutation({
    mutationFn: (next: ProxyDraft) =>
      api.proxy.updateConfig({
        enabled: next.enabled,
        adminEmail: next.adminEmail.trim() || null,
        caddyImage: next.caddyImage.trim(),
      }),
    onSuccess: next => {
      qc.setQueryData(['proxy', 'config'], next)
      qc.invalidateQueries({ queryKey: ['proxy'] })
      setDraft(null)
      toast.success('Proxy settings saved — changes apply immediately.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to save.'),
  })

  function set<K extends keyof ProxyDraft>(key: K, value: ProxyDraft[K]) {
    if (!form) return
    setDraft({ ...form, [key]: value })
  }

  const isPinned = (path: string) => data?.pinnedPaths.includes(path) ?? false
  const pinnedPath = (path: string) => (isPinned(path) ? path : null)

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text">Reverse proxy</h2>
        <p className="mt-0.5 text-[13px] text-text-2">
          The managed Caddy proxy serving your routes. Changes apply immediately — no restart needed.
        </p>
      </div>

      {isLoading || !form ? (
        <Card>
          <CardContent className="flex flex-col gap-4 p-5">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : isError ? (
        <Banner
          tone="danger"
          title="Couldn't load proxy settings"
          action={
            <Button size="sm" variant="secondary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          The reverse-proxy configuration is unavailable.
        </Banner>
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-5 p-5">
            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">Enable reverse proxy</span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Runs a managed Caddy container publishing host ports 80/443 and serving the configured
                  routes with automatic TLS. Disabling stops and removes the container — issued
                  certificates are kept for re-enabling.
                </span>
                {pinnedPath('Watchtower:Proxy:Enabled') && (
                  <span className="mt-1 block">
                    <PinnedNote path="Watchtower:Proxy:Enabled" />
                  </span>
                )}
              </span>
              <Switch
                checked={form.enabled}
                onCheckedChange={v => set('enabled', v)}
                disabled={isPinned('Watchtower:Proxy:Enabled')}
                aria-label="Enable reverse proxy"
              />
            </label>

            <div className="grid gap-4 md:grid-cols-2">
              <Field
                label="ACME email"
                hint="Registered with the certificate authority for expiry notices. Optional but recommended."
              >
                {({ id }) => (
                  <>
                    <Input
                      id={id}
                      mono
                      placeholder="ops@example.com"
                      value={form.adminEmail}
                      onChange={e => set('adminEmail', e.target.value)}
                      disabled={isPinned('Watchtower:Proxy:AdminEmail')}
                    />
                    {pinnedPath('Watchtower:Proxy:AdminEmail') && (
                      <PinnedNote path="Watchtower:Proxy:AdminEmail" />
                    )}
                  </>
                )}
              </Field>
              <Field
                label="Caddy image"
                hint="Applies when the proxy container is next recreated (e.g. after disabling and re-enabling)."
              >
                {({ id }) => (
                  <>
                    <Input
                      id={id}
                      mono
                      placeholder="caddy:2"
                      value={form.caddyImage}
                      onChange={e => set('caddyImage', e.target.value)}
                      disabled={isPinned('Watchtower:Proxy:CaddyImage')}
                    />
                    {pinnedPath('Watchtower:Proxy:CaddyImage') && (
                      <PinnedNote path="Watchtower:Proxy:CaddyImage" />
                    )}
                  </>
                )}
              </Field>
            </div>

            <div className="flex items-center gap-3">
              <Button
                variant="primary"
                size="sm"
                disabled={!dirty || save.isPending}
                loading={save.isPending}
                onClick={() => draft && save.mutate(draft)}
              >
                Save proxy
              </Button>
              {dirty && <span className="text-[13px] text-text-2">Unsaved changes</span>}
            </div>
          </CardContent>
        </Card>
      )}
    </section>
  )
}

// ── Auth card (restart-required toggle) ───────────────────────────────────────

interface AuthDraft {
  enabled: boolean
  host: string
  sessionLifetimeHours: number
  absoluteSessionLifetimeDays: number
}

function toAuthDraft(config: AuthConfig): AuthDraft {
  return {
    enabled: config.enabled,
    host: config.host ?? '',
    sessionLifetimeHours: config.sessionLifetimeHours,
    absoluteSessionLifetimeDays: config.absoluteSessionLifetimeDays,
  }
}

function AuthCard() {
  const qc = useQueryClient()
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['system', 'authConfig'],
    queryFn: api.system.getAuthConfig,
    staleTime: 60_000,
  })

  const [draft, setDraft] = useState<AuthDraft | null>(null)
  const form = draft ?? (data ? toAuthDraft(data) : null)
  const dirty = draft != null && data != null && JSON.stringify(draft) !== JSON.stringify(toAuthDraft(data))

  const save = useMutation({
    mutationFn: (next: AuthDraft) =>
      api.system.updateAuthConfig({
        enabled: next.enabled,
        host: next.host.trim() || null,
        sessionLifetimeHours: next.sessionLifetimeHours,
        absoluteSessionLifetimeDays: next.absoluteSessionLifetimeDays,
      }),
    onSuccess: next => {
      qc.setQueryData(['system', 'authConfig'], next)
      setDraft(null)
      toast.success(
        next.restartRequired
          ? 'Saved. Restart Watchtower to apply the authentication change.'
          : 'Authentication settings saved.',
      )
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to save.'),
  })

  function set<K extends keyof AuthDraft>(key: K, value: AuthDraft[K]) {
    if (!form) return
    setDraft({ ...form, [key]: value })
  }

  const isPinned = (path: string) => data?.pinnedPaths.includes(path) ?? false
  const pinnedPath = (path: string) => (isPinned(path) ? path : null)

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text">Authentication</h2>
        <p className="mt-0.5 text-[13px] text-text-2">
          Watchtower's login and access control. Enabling or disabling takes effect after a restart;
          the other values apply live.
        </p>
      </div>

      {isLoading || !form ? (
        <Card>
          <CardContent className="flex flex-col gap-4 p-5">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : isError ? (
        <Banner
          tone="danger"
          title="Couldn't load authentication settings"
          action={
            <Button size="sm" variant="secondary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          The authentication configuration is unavailable.
        </Banner>
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-5 p-5">
            {data?.restartRequired && (
              <Banner tone="warn" title="Restart required">
                Authentication is {data.active ? 'active' : 'inactive'} in the running process, but is
                configured {data.enabled ? 'on' : 'off'}. Restart Watchtower to apply the change. (The
                env var WATCHTOWER__AUTH__ENABLED always wins if you need an escape hatch.)
              </Banner>
            )}

            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">Enable authentication</span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Watchtower manages users and enforces access policy. Requires an enabled admin account
                  (Users page) so you can log back in after the restart.
                </span>
                {pinnedPath('Watchtower:Auth:Enabled') && (
                  <span className="mt-1 block">
                    <PinnedNote path="Watchtower:Auth:Enabled" />
                  </span>
                )}
              </span>
              <Switch
                checked={form.enabled}
                onCheckedChange={v => set('enabled', v)}
                disabled={isPinned('Watchtower:Auth:Enabled')}
                aria-label="Enable authentication"
              />
            </label>

            <Field
              label="Login host"
              hint="Public hostname of the central login page (bare host, no scheme). Optional while only Watchtower's own UI is protected."
            >
              {({ id }) => (
                <>
                  <Input
                    id={id}
                    mono
                    placeholder="watchtower.example.com"
                    value={form.host}
                    onChange={e => set('host', e.target.value)}
                    disabled={isPinned('Watchtower:Auth:Host')}
                  />
                  {pinnedPath('Watchtower:Auth:Host') && <PinnedNote path="Watchtower:Auth:Host" />}
                </>
              )}
            </Field>

            <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
              <div className="flex items-center gap-2">
                <span className="text-[13px] text-text-2">Session idle timeout</span>
                <Input
                  type="number"
                  min={1}
                  max={720}
                  value={form.sessionLifetimeHours}
                  onChange={e =>
                    set('sessionLifetimeHours', Math.max(1, Math.min(720, Number(e.target.value) || 1)))
                  }
                  className="w-20 tnum"
                  disabled={isPinned('Watchtower:Auth:SessionLifetimeHours')}
                  aria-label="Session idle lifetime in hours"
                />
                <span className="text-[13px] text-text-2">hours</span>
                {pinnedPath('Watchtower:Auth:SessionLifetimeHours') && (
                  <PinnedNote path="Watchtower:Auth:SessionLifetimeHours" />
                )}
              </div>
              <div className="flex items-center gap-2">
                <span className="text-[13px] text-text-2">Absolute limit</span>
                <Input
                  type="number"
                  min={1}
                  max={365}
                  value={form.absoluteSessionLifetimeDays}
                  onChange={e =>
                    set(
                      'absoluteSessionLifetimeDays',
                      Math.max(1, Math.min(365, Number(e.target.value) || 1)),
                    )
                  }
                  className="w-20 tnum"
                  disabled={isPinned('Watchtower:Auth:AbsoluteSessionLifetimeDays')}
                  aria-label="Absolute session lifetime in days"
                />
                <span className="text-[13px] text-text-2">days</span>
                {pinnedPath('Watchtower:Auth:AbsoluteSessionLifetimeDays') && (
                  <PinnedNote path="Watchtower:Auth:AbsoluteSessionLifetimeDays" />
                )}
              </div>
            </div>

            <div className="flex items-center gap-3">
              <Button
                variant="primary"
                size="sm"
                disabled={!dirty || save.isPending}
                loading={save.isPending}
                onClick={() => draft && save.mutate(draft)}
              >
                Save authentication
              </Button>
              {dirty && <span className="text-[13px] text-text-2">Unsaved changes</span>}
            </div>
          </CardContent>
        </Card>
      )}
    </section>
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

  const canCheck = !!status.detectedImageName
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
                <dd className="truncate font-mono text-text-2" title={status.detectedImageName ?? ''}>
                  {status.detectedImageName ?? (
                    <span className="font-sans italic text-text-3">unknown</span>
                  )}
                </dd>
              </dl>
            </div>
          </div>
        )}

        {/* ── Credential ── */}
        <CredentialRow status={status} onSave={onSave} saving={saving} />

        {/* ── Check error ── */}
        {checkError && (
          <div className="border-t border-border px-4 py-4 md:px-5">
            <Banner tone="danger" title="Update check failed">
              {checkError}
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
    onSave({ credentialId: value === NO_CREDENTIAL ? null : Number(value) })
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
