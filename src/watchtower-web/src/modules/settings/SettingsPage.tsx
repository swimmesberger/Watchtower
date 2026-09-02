import { useEffect, useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link, useRouteContext } from '@tanstack/react-router'
import {
  AlertTriangle,
  CheckCircle2,
  Lock,
  RefreshCw,
  RotateCcw,
  Timer,
} from 'lucide-react'
import { api, BUNDLE_DOWNLOAD_URL, INTERNAL_CA_DOWNLOAD_URL } from '@/lib/api'
import type {
  AuthConfig,
  AutomationConfig,
  BackupConfig,
  BackupEvent,
  BackupProvider,
  MetricsBackend,
  MetricsConfig,
  ProxyConfig,
  ProxyProvider,
  SelfUpdateStatus,
  UpdateSelfConfigRequest,
} from '@/lib/types'
import { describeCron } from '@/lib/cron'
import { lanNameKey, parseLanNames } from '@/lib/lanNames'
import { absoluteTitle, formatBytes, formatUptime, shortDigest, timeAgo } from '@/lib/format'
import { ContainerLogs } from '@/components/container-logs'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { SecretField } from '@/components/ui/secret-field'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusBadge } from '@/components/ui/status-badge'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'
import { RecoveryChecklistCard } from './RecoveryChecklist'

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

      <BackupsCard />

      <InstanceBackupCard />

      {/* Self-hiding: renders only while a restore has left stacks to bring back (ADR-0027). */}
      <RecoveryChecklistCard />

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
          <CardContent className="flex flex-col gap-4">
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
          <CardContent className="flex flex-col gap-5">
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
  database: 'Persisted (database, default)',
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
          <CardContent className="flex flex-col gap-4">
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
          <CardContent className="flex flex-col gap-5">
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

            {form.backend === 'database' && (
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

/** Complete, so a stored provider always has a label to render — including one not offered below. */
const PROVIDER_LABELS: Record<ProxyProvider, string> = {
  yarp: 'Built-in (in-process, terminates 80/443)',
  caddy: 'Caddy container (deprecated)',
  cloudflare: 'Cloudflare Tunnel (no open ports)',
}

/** What the picker offers, default first — the same order the backend accepts them in. */
const SELECTABLE_PROVIDERS: ProxyProvider[] = ['yarp', 'caddy', 'cloudflare']

interface ProxyDraft {
  enabled: boolean
  provider: ProxyProvider
  adminEmail: string
  caddyImage: string
  /** Container ports the ingress listeners bind; empty or 0 turns one off. */
  yarpHttpPort: string
  yarpHttpsPort: string
  yarpAcmeDirectoryUrl: string
  yarpAcmeCaBundlePath: string
  yarpAcmeEabKeyId: string
  /** Only sent when non-empty — an empty field keeps the stored key. */
  yarpAcmeEabHmacKey: string
  yarpRedirectHttpToHttps: boolean
  /**
   * Comma- or newline-separated LAN host names and IPs the internal CA issues port-route leaves for.
   * Not a yarp field: a port route's listener is on Watchtower's own container whichever provider
   * terminates the public domains (ADR-0033 addendum), so it is sent under every provider.
   */
  portRoutesLanNames: string
  cfAccountId: string
  cfZoneId: string
  /** Only sent when non-empty — an empty field keeps the stored token. */
  cfApiToken: string
  cfTunnelName: string
  cfTeamDomain: string
  cfManaged: boolean
  cfCloudflaredImage: string
  cfContainerName: string
  cfAccessEmails: string
  cfAccessEmailDomains: string
  cfAccessGroupIds: string
  cfAccessReusablePolicyIds: string
}

function toProxyDraft(config: ProxyConfig): ProxyDraft {
  return {
    enabled: config.enabled,
    provider: config.provider,
    adminEmail: config.adminEmail ?? '',
    caddyImage: config.caddyImage,
    yarpHttpPort: String(config.yarp.httpPort),
    yarpHttpsPort: String(config.yarp.httpsPort),
    yarpAcmeDirectoryUrl: config.yarp.acmeDirectoryUrl,
    yarpAcmeCaBundlePath: config.yarp.acmeCaBundlePath ?? '',
    yarpAcmeEabKeyId: config.yarp.acmeEabKeyId ?? '',
    yarpAcmeEabHmacKey: '',
    yarpRedirectHttpToHttps: config.yarp.redirectHttpToHttps,
    portRoutesLanNames: config.portRoutes.lanNames,
    cfAccountId: config.cloudflare.accountId ?? '',
    cfZoneId: config.cloudflare.zoneId ?? '',
    cfApiToken: '',
    cfTunnelName: config.cloudflare.tunnelName,
    cfTeamDomain: config.cloudflare.teamDomain ?? '',
    cfManaged: config.cloudflare.managed,
    cfCloudflaredImage: config.cloudflare.cloudflaredImage,
    cfContainerName: config.cloudflare.cloudflaredContainerName ?? '',
    cfAccessEmails: config.cloudflare.accessAllowedEmails,
    cfAccessEmailDomains: config.cloudflare.accessAllowedEmailDomains,
    cfAccessGroupIds: config.cloudflare.accessGroupIds,
    cfAccessReusablePolicyIds: config.cloudflare.accessReusablePolicyIds,
  }
}

// ── LAN name suggestions ──────────────────────────────────────────────────────
// A comma-separated list of addresses is a thing an operator has to know before they can type it, and
// on a home LAN the answer is sitting in places nobody thinks to look. So they are offered as chips
// for the LAN names setting of ADR-0033 decision 6. Nothing is ever saved by a click — the value lands
// in the field and the ordinary Save writes it.
//
// Every rule about what may be suggested lives on the server, this side renders what it is sent. That
// includes the address in the address bar: it is sent up as the hint and comes back as a candidate, so
// a browser holding `host.docker.internal` or `my_nas` — both legal there, neither nameable by a
// certificate — produces no chip rather than one whose click makes the Save fail.

/** Appends one name to the field, comma-separated, leaving what is already typed exactly as typed. */
function appendLanName(raw: string, name: string): string {
  const existing = raw.trim().replace(/,+$/, '').trim()
  return existing.length === 0 ? name : `${existing}, ${name}`
}

/**
 * The host in the address bar, without the brackets an IPv6 authority is written in — the hint the
 * server turns into candidates. Host only: a port is not part of any name a certificate carries.
 */
function browserHost(): string {
  const host = typeof window === 'undefined' ? '' : window.location.hostname
  return host.startsWith('[') && host.endsWith(']') ? host.slice(1, -1) : host
}

/**
 * The suggestion chips under the LAN names field. Renders nothing at all while the query is in flight
 * or after it fails: this is a convenience, and a convenience that could not be computed has nothing to
 * say — least of all a banner over a field somebody is typing in.
 */
function LanNameSuggestionChips({
  value,
  onAdd,
}: {
  value: string
  onAdd: (name: string) => void
}) {
  const hint = browserHost()
  // Mounted only inside the proxy-enabled block, which is what gates the call. Kept a long time and
  // not refetched on focus — the answer is about the shape of a LAN, which does not move while
  // somebody edits a text field.
  const { data } = useQuery({
    queryKey: ['proxy', 'lan-name-suggestions', hint],
    queryFn: () => api.proxy.suggestLanNames(hint || null),
    staleTime: 5 * 60_000,
    refetchOnWindowFocus: false,
  })

  // The server excludes what the *saved* setting holds; this drops what the operator has typed since,
  // so a chip disappears the moment it is clicked rather than at the next save.
  const listed = new Set(parseLanNames(value).map(lanNameKey))
  const chips = (data ?? []).filter(candidate => !listed.has(lanNameKey(candidate.value)))
  if (chips.length === 0) return null

  return (
    <div className="mt-2 flex flex-wrap items-center gap-1.5">
      <span className="text-[13px] text-text-2">Suggestions:</span>
      {chips.map(chip => (
        <Tooltip key={chip.value} label={chip.detail}>
          <button
            type="button"
            onClick={() => onAdd(chip.value)}
            className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2.5 py-1 font-mono text-[11px] text-text-2 hover:bg-surface-3 hover:text-text"
          >
            {chip.verified && <CheckCircle2 className="size-3 text-ok" aria-hidden />}
            {chip.value}
          </button>
        </Tooltip>
      ))}
    </div>
  )
}

/** A port field on the wire: an empty or unparseable field is the listener turned off. */
function portValue(raw: string): number {
  const port = Number.parseInt(raw.trim(), 10)
  return Number.isFinite(port) && port > 0 ? port : 0
}

function ProxyCard() {
  const qc = useQueryClient()
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['proxy', 'config'],
    queryFn: api.proxy.getConfig,
    staleTime: 60_000,
  })

  // Whether Watchtower's own CA exists yet, which is what decides whether there is a root to offer.
  // Not the same question as "are LAN names configured": the root is minted the first time a port
  // route needs a leaf, so between setting the names and creating one the download endpoint 404s.
  const { data: internalCa } = useQuery({
    queryKey: ['proxy', 'internal-ca'],
    queryFn: api.proxy.getInternalCa,
    // Whenever the proxy is on, not only under yarp: port routes — and therefore the root an operator
    // has to import — exist alongside Caddy and the tunnel too.
    enabled: data?.enabled === true,
    staleTime: 60_000,
  })

  const [draft, setDraft] = useState<ProxyDraft | null>(null)
  const form = draft ?? (data ? toProxyDraft(data) : null)
  const dirty = draft != null && data != null && JSON.stringify(draft) !== JSON.stringify(toProxyDraft(data))

  const save = useMutation({
    mutationFn: (next: ProxyDraft) =>
      api.proxy.updateConfig({
        enabled: next.enabled,
        provider: next.provider,
        adminEmail: next.adminEmail.trim() || null,
        caddyImage: next.caddyImage.trim(),
        // The yarp fields are sent only while the in-process provider is the one selected. The server
        // validates any value this request supplies, so sending them from a caddy/cloudflare save would
        // let a stale CA bundle path — a file that vanished across a remount, say — refuse the very two
        // saves an operator reaches for when the certificate plane is broken: "disable the proxy" and
        // "switch back to caddy". Omitted, the stored values are simply kept.
        // An empty field means the listener is off, which is what 0 says on the wire.
        yarpHttpPort: next.provider === 'yarp' ? portValue(next.yarpHttpPort) : null,
        yarpHttpsPort: next.provider === 'yarp' ? portValue(next.yarpHttpsPort) : null,
        yarpAcmeDirectoryUrl: next.provider === 'yarp' ? next.yarpAcmeDirectoryUrl.trim() : null,
        yarpAcmeCaBundlePath: next.provider === 'yarp' ? next.yarpAcmeCaBundlePath.trim() : null,
        yarpAcmeEabKeyId: next.provider === 'yarp' ? next.yarpAcmeEabKeyId.trim() : null,
        // Empty also means "keep what is stored", which is how a secret survives a save it did not replace.
        yarpAcmeEabHmacKey:
          next.provider === 'yarp' ? next.yarpAcmeEabHmacKey.trim() || null : null,
        yarpRedirectHttpToHttps: next.provider === 'yarp' ? next.yarpRedirectHttpToHttps : null,
        // Sent under every provider, unlike the yarp fields above: the LAN names decide what the
        // internal CA issues for, and the port routes it issues for are served whichever provider
        // terminates the domains. Empty is a real value here — it means the internal CA is unused — so
        // the field is sent as typed rather than coalesced away, and clearing it is a save like any other.
        portRoutesLanNames: next.portRoutesLanNames.trim(),
        cloudflareAccountId: next.cfAccountId.trim() || null,
        cloudflareZoneId: next.cfZoneId.trim() || null,
        cloudflareApiToken: next.cfApiToken.trim() || null,
        cloudflareTunnelName: next.cfTunnelName.trim() || null,
        cloudflareTeamDomain: next.cfTeamDomain.trim(),
        cloudflareManaged: next.cfManaged,
        cloudflaredImage: next.cfCloudflaredImage.trim() || null,
        cloudflaredContainerName: next.cfContainerName.trim() || null,
        cloudflareAccessAllowedEmails: next.cfAccessEmails.trim(),
        cloudflareAccessAllowedEmailDomains: next.cfAccessEmailDomains.trim(),
        cloudflareAccessGroupIds: next.cfAccessGroupIds.trim(),
        cloudflareAccessReusablePolicyIds: next.cfAccessReusablePolicyIds.trim(),
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
          How your routes reach the internet: the built-in proxy Watchtower runs in its own process, a
          Caddy container, or a Cloudflare Tunnel. Changes apply immediately — no restart needed.
        </p>
      </div>

      {isLoading || !form ? (
        <Card>
          <CardContent className="flex flex-col gap-4">
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
          <CardContent className="flex flex-col gap-5">
            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">Enable reverse proxy</span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Serves the configured routes through the selected provider. Disabling tears the
                  provider's data plane down — certificates, the tunnel and DNS records are kept for
                  re-enabling.
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

            <Field
              label="Provider"
              hint="Which backend serves your public domains. The built-in provider terminates TLS in Watchtower's own process — publish 80:8081 and 443:8443 on this container's ingress endpoints (8080 stays the management plane and should be bound to a private interface). Caddy is deprecated and kept for existing installs; it runs as a sibling container holding the host's ports 80/443. Cloudflare Tunnel needs no open ports — TLS terminates at Cloudflare's edge and access can be gated by Zero Trust. LAN port routes work with all three."
            >
              {({ id }) => (
                <>
                  <Select
                    value={form.provider}
                    onValueChange={v => set('provider', v as ProxyProvider)}
                    disabled={isPinned('Watchtower:Proxy:Provider')}
                  >
                    <SelectTrigger id={id}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {SELECTABLE_PROVIDERS.map(p => (
                        <SelectItem key={p} value={p}>
                          {PROVIDER_LABELS[p]}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  {pinnedPath('Watchtower:Proxy:Provider') && (
                    <PinnedNote path="Watchtower:Proxy:Provider" />
                  )}
                </>
              )}
            </Field>

            {/* Both certificate-issuing providers register this address with the CA, so it lives
                above their blocks rather than being duplicated inside each. Cloudflare's edge
                terminates TLS and never asks for one. */}
            {form.provider !== 'cloudflare' && (
              <Field
                label="ACME email"
                hint="Registered with the certificate authority for expiry notices."
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
            )}

            {form.provider === 'caddy' && (
              <div className="flex flex-col gap-4">
                <p className="text-[13px] text-text-2">
                  The Caddy provider is deprecated and kept for installations already running on it.
                  The built-in provider does the same job in Watchtower's own process — no sibling
                  container, no control network, and route status reflects real certificate state.
                </p>
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
            )}

            {form.provider === 'yarp' && (
              <div className="flex flex-col gap-4">
                {form.enabled && data && !data.yarp.httpsListenerBound && (
                  <Banner tone="warn" title="No HTTPS ingress">
                    {data.yarp.httpsPort === 0 ? (
                      <>
                        Routes resolve and are served, but over plain HTTP only — the TLS ingress port is
                        set to 0. Give it a port below (8443 is the default) and publish it on
                        Watchtower's container as <span className="font-mono">443:8443</span>, alongside{' '}
                        <span className="font-mono">80:8081</span>, which ACME HTTP-01 validation needs.
                      </>
                    ) : (
                      <>
                        The TLS ingress listener is not bound, so routes are served over plain HTTP only.
                        Check the container log for a bind failure on port{' '}
                        <span className="font-mono">{data.yarp.httpsPort}</span>, and that it is published
                        as <span className="font-mono">443:{data.yarp.httpsPort}</span> on Watchtower's
                        container.
                      </>
                    )}
                  </Banner>
                )}
                <div className="grid gap-4 md:grid-cols-2">
                  <Field
                    label="Ingress HTTP port"
                    hint="Container port for plain-HTTP ingress — ACME HTTP-01 validation and the plain half of the proxy. Publish it as 80:<port>. 0 turns the listener off. Changes apply immediately: Kestrel rebinds."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          type="number"
                          min={0}
                          max={65535}
                          placeholder="8081"
                          value={form.yarpHttpPort}
                          onChange={e => set('yarpHttpPort', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Yarp:HttpPort')}
                        />
                        {pinnedPath('Watchtower:Proxy:Yarp:HttpPort') && (
                          <PinnedNote path="Watchtower:Proxy:Yarp:HttpPort" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field
                    label="Ingress HTTPS port"
                    hint="Container port for the routed traffic, one certificate per SNI name. Publish it as 443:<port>. 0 turns TLS ingress off — for when something else terminates TLS in front. Changes apply immediately: Kestrel rebinds."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          type="number"
                          min={0}
                          max={65535}
                          placeholder="8443"
                          value={form.yarpHttpsPort}
                          onChange={e => set('yarpHttpsPort', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Yarp:HttpsPort')}
                        />
                        {pinnedPath('Watchtower:Proxy:Yarp:HttpsPort') && (
                          <PinnedNote path="Watchtower:Proxy:Yarp:HttpsPort" />
                        )}
                      </>
                    )}
                  </Field>
                </div>
                <Field
                  label="TLS ingress listener"
                  hint="Follows the port above — no restart needed."
                >
                  {({ id }) => (
                    <p id={id} className="text-[13px] text-text-2">
                      {data?.yarp.httpsListenerBound
                        ? 'Configured'
                        : 'Off — routes are served over plain HTTP only'}
                    </p>
                  )}
                </Field>

                <Field
                  label="ACME directory URL"
                  hint="Use https://acme-staging-v02.api.letsencrypt.org/directory while testing — staging certificates are untrusted but the rate limits are far higher. Any RFC 8555 CA works, including an on-premises step-ca."
                >
                  {({ id }) => (
                    <>
                      <Input
                        id={id}
                        mono
                        placeholder="https://acme-v02.api.letsencrypt.org/directory"
                        value={form.yarpAcmeDirectoryUrl}
                        onChange={e => set('yarpAcmeDirectoryUrl', e.target.value)}
                        disabled={isPinned('Watchtower:Proxy:Yarp:AcmeDirectoryUrl')}
                      />
                      {pinnedPath('Watchtower:Proxy:Yarp:AcmeDirectoryUrl') && (
                        <PinnedNote path="Watchtower:Proxy:Yarp:AcmeDirectoryUrl" />
                      )}
                    </>
                  )}
                </Field>

                <Field
                  label="ACME CA bundle path"
                  hint="Absolute path to a PEM file of roots to trust in addition to the system store, when the directory above is an internal CA whose root this image doesn't ship. Leave empty for Let's Encrypt."
                >
                  {({ id }) => (
                    <>
                      <Input
                        id={id}
                        mono
                        placeholder="/data/acme-ca.pem"
                        value={form.yarpAcmeCaBundlePath}
                        onChange={e => set('yarpAcmeCaBundlePath', e.target.value)}
                        disabled={isPinned('Watchtower:Proxy:Yarp:AcmeCaBundlePath')}
                      />
                      {pinnedPath('Watchtower:Proxy:Yarp:AcmeCaBundlePath') && (
                        <PinnedNote path="Watchtower:Proxy:Yarp:AcmeCaBundlePath" />
                      )}
                    </>
                  )}
                </Field>

                <div className="grid gap-4 md:grid-cols-2">
                  <Field
                    label="EAB key id"
                    hint="External Account Binding, for a CA that binds accounts to an existing customer record (ZeroSSL, Sectigo, many internal CAs). Set together with the HMAC key, or leave both empty."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="kid-from-your-ca"
                          value={form.yarpAcmeEabKeyId}
                          onChange={e => set('yarpAcmeEabKeyId', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Yarp:AcmeEabKeyId')}
                        />
                        {pinnedPath('Watchtower:Proxy:Yarp:AcmeEabKeyId') && (
                          <PinnedNote path="Watchtower:Proxy:Yarp:AcmeEabKeyId" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field
                    label="EAB HMAC key"
                    hint={
                      isPinned('Watchtower:Proxy:Yarp:AcmeEabHmacKey')
                        ? 'The key is set via the environment and cannot be changed here.'
                        : data?.yarp.hasAcmeEabHmacKey
                          ? 'A key is stored. Leave blank to keep it; enter a new one to replace it.'
                          : 'Base64url-encoded, as the CA hands it out.'
                    }
                  >
                    {() => (
                      <>
                        <SecretField
                          value={form.yarpAcmeEabHmacKey}
                          copyable={false}
                          placeholder={
                            data?.yarp.hasAcmeEabHmacKey ? '••••••••  (stored)' : 'Paste the HMAC key'
                          }
                          onChange={v => set('yarpAcmeEabHmacKey', v)}
                          readOnly={isPinned('Watchtower:Proxy:Yarp:AcmeEabHmacKey')}
                          aria-label="ACME EAB HMAC key"
                        />
                        {pinnedPath('Watchtower:Proxy:Yarp:AcmeEabHmacKey') && (
                          <PinnedNote path="Watchtower:Proxy:Yarp:AcmeEabHmacKey" />
                        )}
                      </>
                    )}
                  </Field>
                </div>

                <label className="flex items-start justify-between gap-4">
                  <span className="min-w-0">
                    <span className="block text-[13px] font-medium text-text">
                      Redirect HTTP to HTTPS
                    </span>
                    <span className="mt-0.5 block text-[13px] text-text-2">
                      Turn this off only when another TLS terminator (a load balancer, a cloud ingress)
                      sits in front of Watchtower and already speaks HTTPS to the visitor — redirecting
                      again would loop.
                    </span>
                    {pinnedPath('Watchtower:Proxy:Yarp:RedirectHttpToHttps') && (
                      <span className="mt-1 block">
                        <PinnedNote path="Watchtower:Proxy:Yarp:RedirectHttpToHttps" />
                      </span>
                    )}
                  </span>
                  <Switch
                    checked={form.yarpRedirectHttpToHttps}
                    onCheckedChange={v => set('yarpRedirectHttpToHttps', v)}
                    disabled={isPinned('Watchtower:Proxy:Yarp:RedirectHttpToHttps')}
                    aria-label="Redirect HTTP to HTTPS"
                  />
                </label>
              </div>
            )}

            {/* Its own section, and outside every provider block: a port route is a TLS listener on
                Watchtower's own container, so it is served alongside Caddy and the tunnel exactly as it
                is under the built-in provider (ADR-0033 addendum). Only the proxy being on gates it. */}
            {form.enabled && (
              <div className="flex flex-col gap-4">
                <div>
                  <h3 className="text-sm font-medium text-text">LAN port routes</h3>
                  <p className="mt-1 text-[13px] text-text-2">
                    Reach a service over HTTPS on a LAN address with no domain and no public DNS:
                    Watchtower binds one port per route and presents a certificate from its own CA.
                    Works with every provider above.
                  </p>
                </div>
                <Field
                  label="LAN names"
                  hint="The hostnames and IPs you type in the browser — every port-route certificate carries all of them (e.g. nas.lan, 192.168.1.10). Comma- or newline-separated. Leave empty if you have no LAN-only routes."
                >
                  {({ id }) => (
                    <>
                      <Input
                        id={id}
                        mono
                        placeholder="nas.lan, 192.168.1.10"
                        value={form.portRoutesLanNames}
                        onChange={e => set('portRoutesLanNames', e.target.value)}
                        disabled={isPinned('Watchtower:Proxy:PortRoutes:LanNames')}
                      />
                      {pinnedPath('Watchtower:Proxy:PortRoutes:LanNames') && (
                        <PinnedNote path="Watchtower:Proxy:PortRoutes:LanNames" />
                      )}
                      {/* Not offered when an environment variable pins the field: a chip whose click
                          the input would refuse is an invitation to a dead end. */}
                      {!isPinned('Watchtower:Proxy:PortRoutes:LanNames') && (
                        <LanNameSuggestionChips
                          value={form.portRoutesLanNames}
                          onAdd={name =>
                            set('portRoutesLanNames', appendLanName(form.portRoutesLanNames, name))
                          }
                        />
                      )}
                      {/* Only once the CA exists, which is the moment there is something to download:
                          the root is minted on the first port route's behalf, and the endpoint 404s
                          until then. */}
                      {internalCa?.present === true && (
                        <p className="mt-1.5 text-[13px] text-text-2">
                          <a
                            href={INTERNAL_CA_DOWNLOAD_URL}
                            download
                            className="text-brand hover:underline"
                          >
                            Download the internal CA root
                          </a>{' '}
                          and import it into each device's trust store, so these addresses validate.
                        </p>
                      )}
                    </>
                  )}
                </Field>
              </div>
            )}

            {form.provider === 'cloudflare' && (
              <div className="flex flex-col gap-4">
                <p className="text-[13px] text-text-2">
                  Watchtower configures a remotely-managed tunnel: it pushes one public hostname per
                  route and creates the matching proxied DNS records in your zone. The API token needs
                  the <span className="font-mono">Cloudflare Tunnel:Edit</span> and{' '}
                  <span className="font-mono">DNS:Edit</span> permissions.
                </p>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field
                    label="Account ID"
                    hint="32 hex characters. In the Cloudflare dashboard, open any domain — it's in the “API” panel on the Overview page (and in the dash.cloudflare.com URL)."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="e.g. 372e67954025e0ba6aaa6d586b9e0b59"
                          value={form.cfAccountId}
                          onChange={e => set('cfAccountId', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:AccountId')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:AccountId') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:AccountId" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field
                    label="Zone ID"
                    hint="32 hex characters, in the same “API” panel — open the domain your routes live under, since every domain (zone) has its own ID."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="e.g. 023e105f4ecef8ad9ca31a8372d0c353"
                          value={form.cfZoneId}
                          onChange={e => set('cfZoneId', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:ZoneId')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:ZoneId') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:ZoneId" />
                        )}
                      </>
                    )}
                  </Field>
                </div>
                <Field
                  label="API token"
                  hint={
                    isPinned('Watchtower:Proxy:Cloudflare:ApiToken')
                      ? 'The token is set via the environment and cannot be changed here.'
                      : data?.cloudflare.hasApiToken
                        ? 'A token is stored. Leave blank to keep it; enter a new one to replace it.'
                        : 'Token with Cloudflare Tunnel:Edit and DNS:Edit. Validated against the API on save.'
                  }
                >
                  {() => (
                    <>
                      <SecretField
                        value={form.cfApiToken}
                        copyable={false}
                        placeholder={
                          data?.cloudflare.hasApiToken ? '••••••••  (unchanged)' : 'Paste an API token'
                        }
                        onChange={v => set('cfApiToken', v)}
                        readOnly={isPinned('Watchtower:Proxy:Cloudflare:ApiToken')}
                        aria-label="Cloudflare API token"
                      />
                      {pinnedPath('Watchtower:Proxy:Cloudflare:ApiToken') && (
                        <PinnedNote path="Watchtower:Proxy:Cloudflare:ApiToken" />
                      )}
                    </>
                  )}
                </Field>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field
                    label="Tunnel name"
                    hint="Found (or created, in managed mode) by name — match your existing tunnel when you run cloudflared yourself."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="watchtower"
                          value={form.cfTunnelName}
                          onChange={e => set('cfTunnelName', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:TunnelName')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:TunnelName') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:TunnelName" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field
                    label="Zero Trust team"
                    hint="Team name or {team}.cloudflareaccess.com. Deploys then inject WATCHTOWER_AUTH_JWKS_URL so apps verify Cf-Access-Jwt-Assertion without hard-coding the issuer."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="myteam"
                          value={form.cfTeamDomain}
                          onChange={e => set('cfTeamDomain', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:TeamDomain')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:TeamDomain') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:TeamDomain" />
                        )}
                      </>
                    )}
                  </Field>
                </div>

                <div className="grid gap-4 md:grid-cols-2">
                  <Field
                    label="Access: allowed emails"
                    hint="Comma-separated. Admitted by the Zero Trust Access app of every route with access mode 'Authenticated'. Restricted routes use their own grants (emails of granted users/groups) instead."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="you@example.com, other@example.com"
                          value={form.cfAccessEmails}
                          onChange={e => set('cfAccessEmails', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:AccessAllowedEmails')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:AccessAllowedEmails') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:AccessAllowedEmails" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field
                    label="Access: allowed email domains"
                    hint="Comma-separated, e.g. example.com — anyone with a matching email may pass. Requires the token to also carry Access: Apps and Policies:Edit."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="example.com"
                          value={form.cfAccessEmailDomains}
                          onChange={e => set('cfAccessEmailDomains', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:AccessAllowedEmailDomains')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:AccessAllowedEmailDomains') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:AccessAllowedEmailDomains" />
                        )}
                      </>
                    )}
                  </Field>
                </div>

                <div className="grid gap-4 md:grid-cols-2">
                  <Field
                    label="Access: group ids"
                    hint="Comma-separated Zero Trust Access group ids (UUIDs). The natural fit when your allow-list already lives in an Access group — e.g. your Entra ID users."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
                          value={form.cfAccessGroupIds}
                          onChange={e => set('cfAccessGroupIds', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:AccessGroupIds')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:AccessGroupIds') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:AccessGroupIds" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field
                    label="Access: reusable policy ids"
                    hint="Comma-separated ids of existing reusable Access policies (e.g. your dashboard-maintained default policy), attached to every Authenticated route's app."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="policy id"
                          value={form.cfAccessReusablePolicyIds}
                          onChange={e => set('cfAccessReusablePolicyIds', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:AccessReusablePolicyIds')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:AccessReusablePolicyIds') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:AccessReusablePolicyIds" />
                        )}
                      </>
                    )}
                  </Field>
                </div>

                <label className="flex items-start justify-between gap-4">
                  <span className="min-w-0">
                    <span className="block text-[13px] font-medium text-text">
                      Let Watchtower run cloudflared
                    </span>
                    <span className="mt-0.5 block text-[13px] text-text-2">
                      Watchtower creates and supervises a cloudflared container over the Docker socket —
                      the simplest setup. Turn off if you already run cloudflared yourself; Watchtower
                      then only manages the tunnel's remote configuration and DNS.
                    </span>
                    {pinnedPath('Watchtower:Proxy:Cloudflare:Managed') && (
                      <span className="mt-1 block">
                        <PinnedNote path="Watchtower:Proxy:Cloudflare:Managed" />
                      </span>
                    )}
                  </span>
                  <Switch
                    checked={form.cfManaged}
                    onCheckedChange={v => set('cfManaged', v)}
                    disabled={isPinned('Watchtower:Proxy:Cloudflare:Managed')}
                    aria-label="Let Watchtower run cloudflared"
                  />
                </label>

                {form.cfManaged ? (
                  <Field label="cloudflared image">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="cloudflare/cloudflared:latest"
                          value={form.cfCloudflaredImage}
                          onChange={e => set('cfCloudflaredImage', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:CloudflaredImage')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:CloudflaredImage') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:CloudflaredImage" />
                        )}
                      </>
                    )}
                  </Field>
                ) : (
                  <Field
                    label="Your cloudflared container"
                    hint="Optional: the name of your cloudflared container on this Docker host. Watchtower connects it to the per-stack ingress networks so the generated service URLs resolve — it never creates or removes it. Leave empty if cloudflared runs elsewhere."
                  >
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="cloudflared"
                          value={form.cfContainerName}
                          onChange={e => set('cfContainerName', e.target.value)}
                          disabled={isPinned('Watchtower:Proxy:Cloudflare:CloudflaredContainerName')}
                        />
                        {pinnedPath('Watchtower:Proxy:Cloudflare:CloudflaredContainerName') && (
                          <PinnedNote path="Watchtower:Proxy:Cloudflare:CloudflaredContainerName" />
                        )}
                      </>
                    )}
                  </Field>
                )}
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

// ── Backups card (ADR-0016) ───────────────────────────────────────────────────

interface BackupDraft {
  enabled: boolean
  cron: string
  instanceName: string
  retentionDays: string
  retentionMaxCount: string
  encryptionPassphrase: string
  helperImage: string
  provider: BackupProvider
  sftpHost: string
  sftpPort: string
  sftpUsername: string
  sftpPassword: string
  sftpPrivateKey: string
  sftpPrivateKeyPassphrase: string
  sftpBasePath: string
  localBasePath: string
}

/**
 * Live reading of the schedule field, in the same words the server puts in the audit trail. Shapes
 * the describer doesn't recognise are still valid cron — the server has the final say, so say so
 * rather than crying invalid.
 */
function CronPreview({ expression }: { expression: string }) {
  const fields = expression.trim().split(/\s+/).filter(f => f.length > 0)
  if (fields.length !== 5) {
    return (
      <p className="text-[13px] text-danger">
        Needs exactly five fields: minute hour day-of-month month day-of-week.
      </p>
    )
  }
  const described = describeCron(expression)
  return (
    <p className="text-[13px] text-text-2">
      {described
        ? described.charAt(0).toUpperCase() + described.slice(1)
        : 'Custom expression — shown as entered.'}
    </p>
  )
}

function toBackupDraft(config: BackupConfig): BackupDraft {
  return {
    enabled: config.enabled,
    cron: config.cron,
    instanceName: config.instanceName ?? '',
    retentionDays: String(config.retentionDays),
    retentionMaxCount: String(config.retentionMaxCount),
    encryptionPassphrase: '',
    helperImage: config.helperImage,
    provider: config.provider,
    sftpHost: config.sftp.host ?? '',
    sftpPort: String(config.sftp.port),
    sftpUsername: config.sftp.username ?? '',
    sftpPassword: '',
    sftpPrivateKey: '',
    sftpPrivateKeyPassphrase: '',
    sftpBasePath: config.sftp.basePath,
    localBasePath: config.localBasePath,
  }
}

function BackupsCard() {
  const qc = useQueryClient()
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['backups', 'config'],
    queryFn: api.backups.getConfig,
    staleTime: 60_000,
  })

  const [draft, setDraft] = useState<BackupDraft | null>(null)
  const form = draft ?? (data ? toBackupDraft(data) : null)
  const dirty =
    draft != null && data != null && JSON.stringify(draft) !== JSON.stringify(toBackupDraft(data))

  const save = useMutation({
    mutationFn: (next: BackupDraft) =>
      api.backups.updateConfig({
        enabled: next.enabled,
        cron: next.cron.trim(),
        instanceName: next.instanceName.trim() || null,
        retentionDays: Number(next.retentionDays) || 0,
        retentionMaxCount: Number(next.retentionMaxCount) || 0,
        helperImage: next.helperImage.trim(),
        provider: next.provider,
        // Secrets: null keeps the stored value; the "(unchanged)" placeholder communicates that.
        encryptionPassphrase: next.encryptionPassphrase || null,
        sftpHost: next.sftpHost.trim() || null,
        sftpPort: Number(next.sftpPort) || null,
        sftpUsername: next.sftpUsername.trim() || null,
        sftpPassword: next.sftpPassword || null,
        sftpPrivateKey: next.sftpPrivateKey.trim() || null,
        sftpPrivateKeyPassphrase: next.sftpPrivateKeyPassphrase || null,
        sftpBasePath: next.sftpBasePath.trim() || null,
        localBasePath: next.localBasePath.trim() || null,
      }),
    onSuccess: next => {
      qc.setQueryData(['backups', 'config'], next)
      setDraft(null)
      toast.success('Backup settings saved — changes apply immediately.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to save.'),
  })

  const testStorage = useMutation({
    mutationFn: api.backups.testStorage,
    onSuccess: description => toast.success(`Storage reachable and writable: ${description}`),
    onError: err => toast.error(err instanceof Error ? err.message : 'Storage test failed.'),
  })

  function set<K extends keyof BackupDraft>(key: K, value: BackupDraft[K]) {
    if (!form) return
    setDraft({ ...form, [key]: value })
  }

  const isPinned = (path: string) => data?.pinnedPaths.includes(path) ?? false
  const pinnedPath = (path: string) => (isPinned(path) ? path : null)

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text">Backups</h2>
        <p className="mt-0.5 text-[13px] text-text-2">
          Scheduled archives of each opted-in stack’s volumes, shipped to external storage with
          retention and optional encryption. Which stacks take part is chosen on each stack’s Backups
          tab.
        </p>
      </div>

      {isLoading || !form ? (
        <Card>
          <CardContent className="flex flex-col gap-4">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : isError ? (
        <Banner
          tone="danger"
          title="Couldn't load backup settings"
          action={
            <Button size="sm" variant="secondary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          The backup configuration is unavailable.
        </Banner>
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-5">
            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">Backup schedule</span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Runs every opted-in stack on the schedule below. Manual “Back up now” works even
                  while this is off.
                </span>
                {pinnedPath('Watchtower:Backup:Enabled') && (
                  <span className="mt-1 block">
                    <PinnedNote path="Watchtower:Backup:Enabled" />
                  </span>
                )}
              </span>
              <Switch
                checked={form.enabled}
                onCheckedChange={v => set('enabled', v)}
                disabled={isPinned('Watchtower:Backup:Enabled')}
                aria-label="Enable the backup schedule"
              />
            </label>

            <div className="grid gap-4 md:grid-cols-2">
              <Field
                className="md:col-span-2"
                label="Schedule"
                hint="Five-field cron — minute hour day-of-month month day-of-week — in server-local time. Examples: “30 3 * * *” (daily at 03:30), “30 3,15 * * *” (03:30 and 15:30), “0 */6 * * *” (every 6 hours). Pick a quiet window — stacks with “stop stateful containers” briefly stop their stateful services. Each stack can override this on its Backups tab."
              >
                {({ id }) => (
                  <>
                    <Input
                      id={id}
                      mono
                      placeholder="30 3 * * *"
                      value={form.cron}
                      onChange={e => set('cron', e.target.value)}
                      disabled={isPinned('Watchtower:Backup:Cron')}
                    />
                    <CronPreview expression={form.cron} />
                    {pinnedPath('Watchtower:Backup:Cron') && (
                      <PinnedNote path="Watchtower:Backup:Cron" />
                    )}
                  </>
                )}
              </Field>
              <Field
                label="Instance name"
                hint="Names this Watchtower in the storage layout and manifests, so several instances can share one target."
              >
                {({ id }) => (
                  <>
                    <Input
                      id={id}
                      mono
                      placeholder={data?.resolvedInstanceName}
                      value={form.instanceName}
                      onChange={e => set('instanceName', e.target.value)}
                      disabled={isPinned('Watchtower:Backup:InstanceName')}
                    />
                    {pinnedPath('Watchtower:Backup:InstanceName') && (
                      <PinnedNote path="Watchtower:Backup:InstanceName" />
                    )}
                  </>
                )}
              </Field>
            </div>

            <div className="grid gap-4 md:grid-cols-2">
              <Field label="Keep backups for (days)" hint="Older backups are deleted after each successful run. 0 keeps forever.">
                {({ id }) => (
                  <>
                    <Input
                      id={id}
                      type="number"
                      min={0}
                      value={form.retentionDays}
                      onChange={e => set('retentionDays', e.target.value)}
                      disabled={isPinned('Watchtower:Backup:RetentionDays')}
                    />
                    {pinnedPath('Watchtower:Backup:RetentionDays') && (
                      <PinnedNote path="Watchtower:Backup:RetentionDays" />
                    )}
                  </>
                )}
              </Field>
              <Field
                label="Keep at most (count)"
                hint="With several runs per day, the age limit alone keeps runs × days archives — cap the count too. Per stack, oldest deleted first. 0 is unlimited."
              >
                {({ id }) => (
                  <>
                    <Input
                      id={id}
                      type="number"
                      min={0}
                      value={form.retentionMaxCount}
                      onChange={e => set('retentionMaxCount', e.target.value)}
                      disabled={isPinned('Watchtower:Backup:RetentionMaxCount')}
                    />
                    {pinnedPath('Watchtower:Backup:RetentionMaxCount') && (
                      <PinnedNote path="Watchtower:Backup:RetentionMaxCount" />
                    )}
                  </>
                )}
              </Field>
            </div>

            <Field
              label="Encryption passphrase"
              hint={
                isPinned('Watchtower:Backup:EncryptionPassphrase')
                  ? 'The passphrase is set via the environment and cannot be changed here.'
                  : data?.hasEncryptionPassphrase
                    ? 'A passphrase is stored — backups are encrypted (OpenSSL-compatible AES-256). Leave blank to keep it. Changing it does not re-encrypt old backups.'
                    : 'Optional. When set, backups are AES-256 encrypted in the OpenSSL enc format — restore needs only stock openssl and this passphrase. Store it somewhere safe: without it, encrypted backups are unrecoverable.'
              }
            >
              {() => (
                <>
                  <SecretField
                    value={form.encryptionPassphrase}
                    copyable={false}
                    placeholder={
                      data?.hasEncryptionPassphrase ? '••••••••  (unchanged)' : 'No encryption'
                    }
                    onChange={v => set('encryptionPassphrase', v)}
                    readOnly={isPinned('Watchtower:Backup:EncryptionPassphrase')}
                    aria-label="Backup encryption passphrase"
                  />
                  {pinnedPath('Watchtower:Backup:EncryptionPassphrase') && (
                    <PinnedNote path="Watchtower:Backup:EncryptionPassphrase" />
                  )}
                </>
              )}
            </Field>

            <Field
              label="Storage provider"
              hint="SFTP works with any SSH-reachable storage (a Hetzner Storage Box, a NAS, another server). Local writes into a directory mounted into the container."
            >
              {({ id }) => (
                <>
                  <Select
                    value={form.provider}
                    onValueChange={v => set('provider', v as BackupProvider)}
                    disabled={isPinned('Watchtower:Backup:Provider')}
                  >
                    <SelectTrigger id={id}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="sftp">SFTP</SelectItem>
                      <SelectItem value="local">Local directory</SelectItem>
                    </SelectContent>
                  </Select>
                  {pinnedPath('Watchtower:Backup:Provider') && (
                    <PinnedNote path="Watchtower:Backup:Provider" />
                  )}
                </>
              )}
            </Field>

            {form.provider === 'sftp' && (
              <div className="flex flex-col gap-4">
                <div className="grid gap-4 md:grid-cols-2">
                  <Field label="Host" hint="e.g. u123456.your-storagebox.de">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          value={form.sftpHost}
                          onChange={e => set('sftpHost', e.target.value)}
                          disabled={isPinned('Watchtower:Backup:Sftp:Host')}
                        />
                        {pinnedPath('Watchtower:Backup:Sftp:Host') && (
                          <PinnedNote path="Watchtower:Backup:Sftp:Host" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field label="Port" hint="22 is the SSH default; Hetzner Storage Boxes use 23.">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          type="number"
                          min={1}
                          max={65535}
                          value={form.sftpPort}
                          onChange={e => set('sftpPort', e.target.value)}
                          disabled={isPinned('Watchtower:Backup:Sftp:Port')}
                        />
                        {pinnedPath('Watchtower:Backup:Sftp:Port') && (
                          <PinnedNote path="Watchtower:Backup:Sftp:Port" />
                        )}
                      </>
                    )}
                  </Field>
                </div>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field label="Username">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          value={form.sftpUsername}
                          onChange={e => set('sftpUsername', e.target.value)}
                          disabled={isPinned('Watchtower:Backup:Sftp:Username')}
                        />
                        {pinnedPath('Watchtower:Backup:Sftp:Username') && (
                          <PinnedNote path="Watchtower:Backup:Sftp:Username" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field
                    label="Password"
                    hint={
                      data?.sftp.hasPassword
                        ? 'A password is stored. Leave blank to keep it.'
                        : 'Optional when a private key is used.'
                    }
                  >
                    {() => (
                      <>
                        <SecretField
                          value={form.sftpPassword}
                          copyable={false}
                          placeholder={data?.sftp.hasPassword ? '••••••••  (unchanged)' : 'Password'}
                          onChange={v => set('sftpPassword', v)}
                          readOnly={isPinned('Watchtower:Backup:Sftp:Password')}
                          aria-label="SFTP password"
                        />
                        {pinnedPath('Watchtower:Backup:Sftp:Password') && (
                          <PinnedNote path="Watchtower:Backup:Sftp:Password" />
                        )}
                      </>
                    )}
                  </Field>
                </div>
                <Field
                  label="Private key"
                  hint={
                    (data?.sftp.hasPrivateKey
                      ? 'A key is stored. Leave blank to keep it; paste a new one to replace it. '
                      : 'Optional alternative to the password: paste the full -----BEGIN … KEY----- block and register the matching public key with the storage. ') +
                    'Ed25519, ECDSA, and RSA keys in OpenSSH, PEM, or PuTTY (.ppk) format are supported — Ed448 is not.'
                  }
                >
                  {({ id }) => (
                    <>
                      <Textarea
                        id={id}
                        mono
                        rows={4}
                        placeholder={
                          data?.sftp.hasPrivateKey
                            ? '(unchanged)'
                            : '-----BEGIN OPENSSH PRIVATE KEY-----'
                        }
                        value={form.sftpPrivateKey}
                        onChange={e => set('sftpPrivateKey', e.target.value)}
                        disabled={isPinned('Watchtower:Backup:Sftp:PrivateKey')}
                      />
                      {pinnedPath('Watchtower:Backup:Sftp:PrivateKey') && (
                        <PinnedNote path="Watchtower:Backup:Sftp:PrivateKey" />
                      )}
                    </>
                  )}
                </Field>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field label="Key passphrase" hint="Only if the private key itself is encrypted.">
                    {() => (
                      <>
                        <SecretField
                          value={form.sftpPrivateKeyPassphrase}
                          copyable={false}
                          placeholder="None"
                          onChange={v => set('sftpPrivateKeyPassphrase', v)}
                          readOnly={isPinned('Watchtower:Backup:Sftp:PrivateKeyPassphrase')}
                          aria-label="SFTP private key passphrase"
                        />
                        {pinnedPath('Watchtower:Backup:Sftp:PrivateKeyPassphrase') && (
                          <PinnedNote path="Watchtower:Backup:Sftp:PrivateKeyPassphrase" />
                        )}
                      </>
                    )}
                  </Field>
                  <Field label="Base directory" hint="Remote directory the backups are rooted in (created if missing).">
                    {({ id }) => (
                      <>
                        <Input
                          id={id}
                          mono
                          placeholder="watchtower-backups"
                          value={form.sftpBasePath}
                          onChange={e => set('sftpBasePath', e.target.value)}
                          disabled={isPinned('Watchtower:Backup:Sftp:BasePath')}
                        />
                        {pinnedPath('Watchtower:Backup:Sftp:BasePath') && (
                          <PinnedNote path="Watchtower:Backup:Sftp:BasePath" />
                        )}
                      </>
                    )}
                  </Field>
                </div>
              </div>
            )}

            {form.provider === 'local' && (
              <Field
                label="Directory"
                hint="A path inside the Watchtower container — mount a second disk or network share there; backups on the same disk as the data protect against little."
              >
                {({ id }) => (
                  <>
                    <Input
                      id={id}
                      mono
                      placeholder="/backups"
                      value={form.localBasePath}
                      onChange={e => set('localBasePath', e.target.value)}
                      disabled={isPinned('Watchtower:Backup:Local:BasePath')}
                    />
                    {pinnedPath('Watchtower:Backup:Local:BasePath') && (
                      <PinnedNote path="Watchtower:Backup:Local:BasePath" />
                    )}
                  </>
                )}
              </Field>
            )}

            <div className="flex items-center gap-3">
              <Button
                variant="primary"
                size="sm"
                disabled={!dirty || save.isPending}
                loading={save.isPending}
                onClick={() => draft && save.mutate(draft)}
              >
                Save backups
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={dirty || testStorage.isPending}
                loading={testStorage.isPending}
                onClick={() => testStorage.mutate()}
                title={dirty ? 'Save first — the test probes the stored settings.' : undefined}
              >
                Test storage
              </Button>
              {dirty && <span className="text-[13px] text-text-2">Unsaved changes</span>}
            </div>
          </CardContent>
        </Card>
      )}
    </section>
  )
}

// ── Watchtower's own database (ADR-0027) ─────────────────────────────────────
// Its own card rather than a section of the backups card: the stack backups are a policy an operator
// configures, this is a thing they *do* — and it carries the run action, the archive list, and (from
// stage 2) the bundle export.

/** How an instance archive's own run is summarised in the card's history list. */
function InstanceRunRow({ event }: { event: BackupEvent }) {
  const finished = event.finishedAt ?? event.startedAt
  return (
    <li className="flex items-baseline justify-between gap-3 py-1.5 text-[13px]">
      <span className="flex min-w-0 items-baseline gap-2">
        <StatusBadge status={event.status} />
        <span className="truncate text-text-2">{event.triggeredBy}</span>
      </span>
      <span className="shrink-0 text-text-3" title={absoluteTitle(finished)}>
        {event.sizeBytes != null && `${formatBytes(event.sizeBytes)} · `}
        {timeAgo(finished)}
      </span>
    </li>
  )
}

function InstanceBackupCard() {
  const qc = useQueryClient()
  // Every action in this card is admin-only on the server (the archive it produces is the instance), so
  // an operator without the role is shown nothing rather than buttons that answer 403.
  const { caps } = useRouteContext({ from: '__root__' })
  const isAdmin = caps.hasRole('Admin')

  const { data, isLoading } = useQuery({
    queryKey: ['backups', 'config'],
    queryFn: api.backups.getConfig,
    staleTime: 60_000,
    enabled: isAdmin,
  })

  // A run in flight is worth watching: it is one dump, so it finishes in seconds to minutes.
  const { data: events } = useQuery({
    queryKey: ['backups', 'events', 'instance'],
    queryFn: () => api.backups.events(undefined, 10, undefined, 'instance'),
    enabled: isAdmin,
    refetchInterval: query => {
      const runs = query.state.data ?? []
      return runs.some(e => e.status === 'queued' || e.status === 'running') ? 2000 : false
    },
  })

  const [container, setContainer] = useState<string | null>(null)
  const containerValue = container ?? data?.selfPostgresContainer ?? ''
  const containerDirty =
    container != null && container.trim() !== (data?.selfPostgresContainer ?? '').trim()

  // Both writes go through the whole-config handler, so the stored values for every other field are
  // resent unchanged — the same shape the backups card posts.
  const save = useMutation({
    mutationFn: (patch: { includeSelf?: boolean; selfPostgresContainer?: string }) => {
      if (!data) throw new Error('Backup settings are still loading.')
      return api.backups.updateConfig({
        enabled: data.enabled,
        cron: data.cron,
        instanceName: data.instanceName,
        retentionDays: data.retentionDays,
        retentionMaxCount: data.retentionMaxCount,
        helperImage: data.helperImage,
        provider: data.provider,
        ...patch,
      })
    },
    onSuccess: next => {
      qc.setQueryData(['backups', 'config'], next)
      setContainer(null)
      toast.success('Saved — changes apply immediately.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Failed to save.'),
  })

  const run = useMutation({
    mutationFn: api.backups.runInstance,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['backups', 'events', 'instance'] })
      toast.success('Backing up Watchtower’s database — the run appears below.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Could not start the backup.'),
  })

  // An export dumps the database and downloads every stack's newest archive, so it is minutes of work
  // and the only honest progress signal is the run itself.
  const exporting = (events ?? []).some(
    e => e.triggeredBy === 'bundle-export' && (e.status === 'queued' || e.status === 'running'),
  )
  const { data: bundle } = useQuery({
    queryKey: ['backups', 'bundle'],
    queryFn: api.backups.getBundleStatus,
    enabled: isAdmin,
    refetchInterval: () => (exporting ? 2000 : false),
  })

  // The polling above stops the moment the run leaves 'running', which is one tick before the finished
  // bundle would have been read — so the transition itself is what asks for it.
  useEffect(() => {
    if (!exporting) void qc.invalidateQueries({ queryKey: ['backups', 'bundle'] })
  }, [exporting, qc])

  const exportBundle = useMutation({
    mutationFn: api.backups.exportBundle,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['backups', 'events', 'instance'] })
      toast.success('Building the bundle — this takes a while for a large estate.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'Could not start the export.'),
  })

  const isPinned = (path: string) => data?.pinnedPaths.includes(path) ?? false
  const noPassphrase = data != null && !data.hasEncryptionPassphrase

  if (!isAdmin) return null

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text">Watchtower’s own database</h2>
        <p className="mt-0.5 text-[13px] text-text-2">
          Everything Watchtower knows — stacks, environment variables, products and releases, routes,
          accounts, certificates and keys — lives in its PostgreSQL database. Backing up your stacks
          without it restores their data but nothing that deploys them.
        </p>
      </div>

      {isLoading || !data ? (
        <Card>
          <CardContent className="flex flex-col gap-4">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-5">
            {noPassphrase && (
              <Banner tone="warn" title="An encryption passphrase is required">
                The dump carries every database role’s password hash, the data-protection key ring and
                every certificate’s private key. Set a passphrase under Backups above and this turns on.
              </Banner>
            )}

            <label className="flex items-start justify-between gap-4">
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-text">
                  Include in the backup schedule
                </span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Dumps the database on the same schedule as the stacks, to{' '}
                  <span className="font-mono text-text-3">{data.instanceDirectory}</span> on the backup
                  storage. Nothing is stopped — the dump runs while Watchtower keeps serving.
                </span>
                {isPinned('Watchtower:Backup:IncludeSelf') && (
                  <span className="mt-1 block">
                    <PinnedNote path="Watchtower:Backup:IncludeSelf" />
                  </span>
                )}
              </span>
              <Switch
                checked={data.includeSelf}
                onCheckedChange={v => save.mutate({ includeSelf: v })}
                disabled={isPinned('Watchtower:Backup:IncludeSelf') || save.isPending}
                aria-label="Include Watchtower's own database in the backup schedule"
              />
            </label>

            <Field
              label="Database container"
              hint="Leave blank to detect it. Set this only when Watchtower cannot tell which container holds its database — several PostgreSQL containers on one daemon and none named by the connection string's host. A managed or host-installed PostgreSQL cannot be backed up here at all."
            >
              {({ id }) => (
                <>
                  <div className="flex items-center gap-2">
                    <Input
                      id={id}
                      mono
                      placeholder="(detected)"
                      value={containerValue}
                      onChange={e => setContainer(e.target.value)}
                      disabled={isPinned('Watchtower:Backup:SelfPostgresContainer')}
                    />
                    {containerDirty && (
                      <Button
                        size="sm"
                        variant="secondary"
                        loading={save.isPending}
                        onClick={() =>
                          save.mutate({ selfPostgresContainer: containerValue.trim() })
                        }
                      >
                        Save
                      </Button>
                    )}
                  </div>
                  {isPinned('Watchtower:Backup:SelfPostgresContainer') && (
                    <PinnedNote path="Watchtower:Backup:SelfPostgresContainer" />
                  )}
                </>
              )}
            </Field>

            <div className="flex items-center gap-3">
              <Button
                variant="secondary"
                size="sm"
                disabled={noPassphrase || run.isPending}
                loading={run.isPending}
                onClick={() => run.mutate()}
                title={noPassphrase ? 'Set an encryption passphrase first.' : undefined}
              >
                Back up Watchtower now
              </Button>
            </div>

            <div className="border-t border-border pt-5">
              <h3 className="text-[13px] font-medium text-text">Full backup bundle</h3>
              <p className="mt-0.5 text-[13px] text-text-2">
                One file holding a fresh dump of this database, the newest archive of every stack, and
                the secrets that live outside the database — everything a new Watchtower needs to become
                this one. Take one before a migration, and keep it somewhere you would keep a password.
              </p>

              <Banner tone="warn" title="The bundle contains your secrets in plain text" className="mt-3">
                The key-protection secret, the backup passphrase and your storage credentials are inside
                it. Anyone who has the file can stand this instance up elsewhere.
              </Banner>

              <div className="mt-3 flex flex-wrap items-center gap-3">
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={noPassphrase || exporting || exportBundle.isPending}
                  loading={exporting || exportBundle.isPending}
                  onClick={() => exportBundle.mutate()}
                  title={noPassphrase ? 'Set an encryption passphrase first.' : undefined}
                >
                  {bundle ? 'Build a new bundle' : 'Build bundle'}
                </Button>

                {exporting ? (
                  <span className="text-[13px] text-text-2">
                    Dumping the database and collecting each stack’s newest archive…
                  </span>
                ) : bundle ? (
                  <>
                    <a
                      href={BUNDLE_DOWNLOAD_URL}
                      className="text-[13px] font-medium text-run underline underline-offset-2"
                    >
                      Download {bundle.fileName}
                    </a>
                    <span className="text-[13px] text-text-3" title={absoluteTitle(bundle.createdAtUtc)}>
                      {formatBytes(bundle.sizeBytes)} · {bundle.stackCount} stack archive
                      {bundle.stackCount === 1 ? '' : 's'} · {timeAgo(bundle.createdAtUtc)}
                    </span>
                  </>
                ) : null}
              </div>

              {!exporting && bundle != null && bundle.missingStackCount > 0 && (
                <p className="mt-2 text-[13px] text-warn">
                  {bundle.missingStackCount} stack
                  {bundle.missingStackCount === 1 ? ' has' : 's have'} no archive on the storage — the
                  bundle carries {bundle.missingStackCount === 1 ? 'its' : 'their'} definition but not{' '}
                  {bundle.missingStackCount === 1 ? 'its' : 'their'} data. Back{' '}
                  {bundle.missingStackCount === 1 ? 'it' : 'them'} up and build again.
                </p>
              )}

              {bundle != null && (
                <p className="mt-2 text-[13px] text-text-3">
                  The bundle is kept in this container, so it is lost when Watchtower restarts — download
                  it now, or build a fresh one later.
                </p>
              )}
            </div>

            <div className="border-t border-border pt-5">
              <h3 className="text-[13px] font-medium text-text">Restore from a bundle</h3>
              <p className="mt-0.5 text-[13px] text-text-2">
                Replaces everything this Watchtower knows with what is in a bundle from another
                instance, then walks you through bringing its stacks back.
              </p>
              <Link
                to="/settings/restore"
                className="mt-3 inline-block text-[13px] font-medium text-run underline underline-offset-2"
              >
                Restore this Watchtower…
              </Link>
            </div>

            {events != null && events.length > 0 && (
              <div>
                <h3 className="text-[13px] font-medium text-text">Recent runs</h3>
                <ul className="mt-1 divide-y divide-border">
                  {events.map(e => (
                    <InstanceRunRow key={e.id} event={e} />
                  ))}
                </ul>
              </div>
            )}
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
          <CardContent className="flex flex-col gap-4">
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
          <CardContent className="flex flex-col gap-5">
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

            {/* Read-only, and first: this is what the operator realm's protected apps actually redirect
                to. It comes from the Watchtower route marked as the login host (ADR-0023); the field
                below is only the fallback used while there is none. */}
            <Field
              label="Operator login host (effective)"
              hint={
                data?.effectiveLoginHost
                  ? 'Where anonymous visitors to protected apps are sent. Change it on the Routes page by marking a Watchtower route as the operator realm’s login host.'
                  : 'No login host: protected apps answer anonymous visitors with 401. Create a Watchtower route on the Routes page and mark it as the operator realm’s login host.'
              }
            >
              {({ id }) => (
                <Input id={id} mono readOnly disabled value={data?.effectiveLoginHost ?? 'none'} />
              )}
            </Field>

            <Field
              label="Fallback login host (operator realm)"
              hint="Used only while no Watchtower route is marked as the operator realm's login host. Normally leave this empty and create a Watchtower route instead — a route is served, gets a certificate and reports its status. Set it when another proxy in front of Watchtower terminates the hostname."
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

        {/* ── Watchtower's own container logs (stdout/stderr), for diagnosing the instance itself ── */}
        {status.containerId && (
          <div className="border-t border-border px-4 py-4 md:px-5">
            <ContainerLogs containerId={status.containerId} containerName="Watchtower logs" />
          </div>
        )}

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
