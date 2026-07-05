import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import {
  Database,
  ExternalLink,
  HardDrive,
  Layers,
  Network,
  ShieldAlert,
  Trash2,
} from 'lucide-react'
import { api } from '@/lib/api'
import type {
  NetworkInfo,
  PublishedPort,
  ResourceLifecycle,
  VolumeInfo,
  VolumeSize,
} from '@/lib/types'
import { formatBytes } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { ExposureBadge } from '@/components/ui/exposure-badge'
import { Skeleton } from '@/components/ui/skeleton'
import { SectionHeader } from '@/components/ui/section-header'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

// Volumes and networks poll on the 30s idle cadence (A7 idle backoff). There is no
// deploy/live signal on this fleet-wide view, so we sit permanently on the idle interval.
const IDLE_POLL_MS = 30_000

// ── lifecycle chip (F4) ──────────────────────────────────────────────────────
const LIFECYCLE_META: Record<ResourceLifecycle, { tone: 'ok' | 'neutral' | 'warn'; label: string }> = {
  live: { tone: 'ok', label: 'live' },
  declared: { tone: 'neutral', label: 'declared' },
  orphaned: { tone: 'warn', label: 'orphaned' },
}

function LifecycleBadge({ lifecycle }: { lifecycle: ResourceLifecycle }) {
  const meta = LIFECYCLE_META[lifecycle]
  return <Badge tone={meta.tone}>{meta.label}</Badge>
}

// Exposure risk ordering: public first, then localhost, then internal-only (spec §4.2).
const EXPOSURE_RANK: Record<string, number> = { public: 0, localhost: 1, none: 2 }

function exposureRank(e: string): number {
  return EXPOSURE_RANK[e] ?? 3
}

function hostBinding(p: PublishedPort): string {
  if (p.publicPort == null) return '—'
  return `${p.hostIp}:${p.publicPort}`
}

export function InfrastructurePage() {
  // A project→stackId map lets every fleet-wide row link back to its stack's tab.
  // Volumes/networks/ports carry only the compose project name, not the stack id.
  const stacksQuery = useQuery({ queryKey: ['stacks'], queryFn: api.stacks.list })
  const stackIdByProject = useMemo(() => {
    const map = new Map<string, number>()
    for (const s of stacksQuery.data ?? []) map.set(s.composeProjectName, s.id)
    return map
  }, [stacksQuery.data])

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-8 px-4 py-6 md:px-6">
      <div>
        <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Infrastructure</h1>
        {/* F10: one plain-language sentence under the header. */}
        <p className="mt-1 text-sm text-text-2">
          Everything Docker holds on this host that isn&apos;t tied to a single stack view —
          storage, networks, and exposure across all stacks.
        </p>
      </div>

      <ExposureSection stackIdByProject={stackIdByProject} />
      <VolumesSection stackIdByProject={stackIdByProject} />
      <NetworksSection stackIdByProject={stackIdByProject} />
    </div>
  )
}

// A link to a stack's specific detail tab (F9 deep-link), or a plain label when the
// project can't be resolved to a known stack (e.g. a project Watchtower doesn't manage).
function StackLink({
  project,
  stackIdByProject,
  tab,
}: {
  project: string | null
  stackIdByProject: Map<string, number>
  tab: 'volumes' | 'networks'
}) {
  if (!project) return <span className="text-text-3">—</span>
  const id = stackIdByProject.get(project)
  if (id == null) {
    return <span className="font-mono text-[13px] text-text-2">{project}</span>
  }
  return (
    <Link
      to="/stacks/$id"
      params={{ id: String(id) }}
      search={{ tab }}
      className="inline-flex items-center gap-1 font-medium text-text hover:text-brand"
    >
      {project}
      <ExternalLink className="size-3.5 shrink-0 text-text-3" aria-hidden />
    </Link>
  )
}

// ── (1) Exposure — all published ports across stacks (the security glance) ──────
function ExposureSection({ stackIdByProject }: { stackIdByProject: Map<string, number> }) {
  // No project filter: the whole-host exposure map.
  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['networks', 'ports', 'fleet'],
    queryFn: () => api.networks.ports(null),
    refetchInterval: IDLE_POLL_MS,
  })

  const published = data?.published ?? []
  const conflicts = data?.conflicts ?? []

  // Public-first, then by host port for a stable read.
  const sorted = useMemo(
    () =>
      [...published].sort(
        (a, b) =>
          exposureRank(a.exposure) - exposureRank(b.exposure) ||
          (a.publicPort ?? Number.MAX_SAFE_INTEGER) - (b.publicPort ?? Number.MAX_SAFE_INTEGER) ||
          a.containerName.localeCompare(b.containerName),
      ),
    [published],
  )

  const publicCount = published.filter((p) => p.exposure === 'public').length

  const columns: DataListColumn<PublishedPort>[] = [
    {
      key: 'container',
      header: 'Container',
      cell: (p) => <span className="font-mono text-[13px] text-text">{p.containerName}</span>,
    },
    {
      key: 'stack',
      header: 'Stack',
      cell: (p) => (
        <StackLink project={p.stackName} stackIdByProject={stackIdByProject} tab="networks" />
      ),
    },
    {
      key: 'port',
      header: 'Port',
      cell: (p) => (
        <span className="tnum font-mono text-[13px] text-text-2">
          {p.privatePort}/{p.protocol}
        </span>
      ),
    },
    {
      key: 'binding',
      header: 'Host binding',
      cell: (p) => (
        <span className="tnum font-mono text-[13px] text-text-2">{hostBinding(p)}</span>
      ),
    },
    {
      key: 'exposure',
      header: 'Exposure',
      align: 'right',
      cell: (p) => (
        <div className="flex justify-end">
          <ExposureBadge exposure={p.exposure} />
        </div>
      ),
    },
  ]

  const renderCard = (p: PublishedPort) => (
    <div className="space-y-2">
      <div className="flex items-start justify-between gap-3">
        <span className="font-mono text-[13px] text-text">{p.containerName}</span>
        <ExposureBadge exposure={p.exposure} />
      </div>
      <p className="text-[13px] text-text-2">
        <StackLink project={p.stackName} stackIdByProject={stackIdByProject} tab="networks" />
      </p>
      <p className="tnum font-mono text-[13px] text-text-2">
        {p.privatePort}/{p.protocol} · {hostBinding(p)}
      </p>
    </div>
  )

  return (
    <section>
      <SectionHeader
        title="Exposure"
        description="Every published port across all stacks, riskiest first — the world-exposure glance."
        action={
          published.length > 0 ? (
            <Badge tone={publicCount > 0 ? 'danger' : 'neutral'}>
              {publicCount} public · {published.length} total
            </Badge>
          ) : undefined
        }
      />

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn’t load the exposure map"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          {(error as Error)?.message ?? 'An unexpected error occurred.'}
        </Banner>
      ) : (
        <div className="space-y-3">
          {/* Port-conflict warning (spec §4.2): ≥2 containers claim the same host ip:port:proto. */}
          {conflicts.map((c) => (
            <Banner
              key={`${c.hostIp}:${c.publicPort}/${c.protocol}`}
              tone="warn"
              icon={ShieldAlert}
              title="Port conflict"
            >
              Port{' '}
              <span className="tnum font-mono">
                {c.publicPort}/{c.protocol}
              </span>{' '}
              on <span className="font-mono">{c.hostIp}</span> is claimed by{' '}
              {c.containerNames.length} containers ({c.containerNames.join(', ')}).
            </Banner>
          ))}

          <DataList
            items={sorted}
            getKey={(p) => `${p.containerId}:${p.hostIp}:${p.publicPort}:${p.privatePort}/${p.protocol}`}
            columns={columns}
            renderCard={renderCard}
            skeletonRows={isLoading ? 4 : undefined}
            emptyState={
              <EmptyState
                icon={ShieldAlert}
                title="No published ports"
                description="No stack publishes a port to the host — nothing is reachable from the host network."
              />
            }
            aria-label="Published ports across all stacks"
          />
        </div>
      )}
    </section>
  )
}

// ── (2) Volumes — per-stack counts + orphan triage ─────────────────────────────
function VolumesSection({
  stackIdByProject,
}: {
  stackIdByProject: Map<string, number>
}) {
  const qc = useQueryClient()

  const { data: volumes = [], isLoading, isError, error, refetch } = useQuery({
    queryKey: ['volumes', 'fleet'],
    queryFn: () => api.volumes.list(null),
    refetchInterval: IDLE_POLL_MS,
  })

  const orphans = useMemo(() => volumes.filter((v) => v.lifecycle === 'orphaned'), [volumes])
  const stackVolumes = useMemo(() => volumes.filter((v) => v.lifecycle !== 'orphaned'), [volumes])

  // Per-stack collapse: group non-orphan volumes by their compose project.
  const perStack = useMemo(() => {
    const groups = new Map<string, { project: string; count: number; live: number }>()
    for (const v of stackVolumes) {
      const key = v.project ?? '—'
      const g = groups.get(key) ?? { project: key, count: 0, live: 0 }
      g.count += 1
      if (v.lifecycle === 'live') g.live += 1
      groups.set(key, g)
    }
    return [...groups.values()].sort((a, b) => a.project.localeCompare(b.project))
  }, [stackVolumes])

  // Orphan sizes auto-load on the Infrastructure page (triage needs them) — §3.5.
  const sizesQuery = useQuery({
    queryKey: ['volumes', 'sizes', 'fleet'],
    queryFn: () => api.volumes.sizes(null),
    // Only meaningful once there are orphans to size; keeps df off when the list is clean.
    enabled: orphans.length > 0,
    staleTime: 60_000,
  })
  const sizeByName = useMemo(() => {
    const map = new Map<string, VolumeSize>()
    for (const s of sizesQuery.data ?? []) map.set(s.name, s)
    return map
  }, [sizesQuery.data])

  const reclaimEstimate = useMemo(
    () => orphans.reduce((sum, v) => sum + (sizeByName.get(v.name)?.sizeBytes ?? 0), 0),
    [orphans, sizeByName],
  )

  const [pendingDelete, setPendingDelete] = useState<VolumeInfo | null>(null)
  const [pruneOpen, setPruneOpen] = useState(false)

  const remove = useMutation({
    mutationFn: (v: VolumeInfo) => api.volumes.remove(v.name),
    onSuccess: (_data, v) => {
      toast.success(`Deleted volume ${v.name}.`)
      qc.invalidateQueries({ queryKey: ['volumes'] })
    },
    onError: (err: Error, v) => {
      toast.error(`Couldn’t delete ${v.name}: ${err.message}`)
    },
    onSettled: () => setPendingDelete(null),
  })

  const prune = useMutation({
    mutationFn: () => api.volumes.pruneOrphans(),
    onSuccess: (res) => {
      const bytes = res.reclaimedBytes
      const reclaimed = bytes != null ? `, reclaimed ${formatBytes(bytes)}` : ''
      toast.success(`Removed ${res.removed.length} volumes${reclaimed}.`)
      qc.invalidateQueries({ queryKey: ['volumes'] })
    },
    onError: (err: Error) => {
      toast.error(`Prune failed: ${err.message}`)
    },
    onSettled: () => setPruneOpen(false),
  })

  // Size cell: while orphan sizes are loading show a skeleton; delete only when refCount==0 (F4).
  function SizeCell({ v }: { v: VolumeInfo }) {
    const size = sizeByName.get(v.name)
    if (size) {
      return <span className="tnum font-mono text-[13px] text-text-2">{formatBytes(size.sizeBytes)}</span>
    }
    if (sizesQuery.isLoading) return <Skeleton variant="line" className="h-4 w-16" />
    return <span className="text-text-3">—</span>
  }

  function DeleteOrphanButton({ v }: { v: VolumeInfo }) {
    const deletable = v.refCount === 0
    if (!deletable) {
      // Guard: an orphan should always have refCount 0, but never offer Delete otherwise.
      return null
    }
    return (
      <Tooltip label="Delete volume">
        <Button
          size="icon-sm"
          variant="ghost"
          aria-label={`Delete ${v.name}`}
          onClick={() => setPendingDelete(v)}
          className="text-text-2 hover:text-danger"
        >
          <Trash2 />
        </Button>
      </Tooltip>
    )
  }

  const orphanColumns: DataListColumn<VolumeInfo>[] = [
    {
      key: 'name',
      header: 'Volume',
      cell: (v) => (
        <span className="block max-w-[36ch] truncate font-mono text-[13px] text-text" title={v.name}>
          {v.name}
        </span>
      ),
    },
    {
      key: 'driver',
      header: 'Driver',
      cell: (v) =>
        v.driver && v.driver !== 'local' ? (
          <Badge tone="neutral">{v.driver}</Badge>
        ) : (
          <span className="text-text-3">local</span>
        ),
    },
    {
      key: 'lifecycle',
      header: 'Status',
      cell: (v) => <LifecycleBadge lifecycle={v.lifecycle} />,
    },
    {
      key: 'size',
      header: 'Size',
      cell: (v) => <SizeCell v={v} />,
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (v) => (
        <div className="flex justify-end">
          <DeleteOrphanButton v={v} />
        </div>
      ),
    },
  ]

  const renderOrphanCard = (v: VolumeInfo) => (
    <div className="space-y-2">
      <div className="flex items-start justify-between gap-3">
        <span className="min-w-0 truncate font-mono text-[13px] text-text" title={v.name}>
          {v.name}
        </span>
        <LifecycleBadge lifecycle={v.lifecycle} />
      </div>
      <div className="flex items-center justify-between gap-3">
        <span className="text-[13px] text-text-2">
          {v.driver && v.driver !== 'local' ? v.driver : 'local'} · <SizeCellInline v={v} />
        </span>
        <DeleteOrphanButton v={v} />
      </div>
    </div>
  )

  function SizeCellInline({ v }: { v: VolumeInfo }) {
    const size = sizeByName.get(v.name)
    if (size) return <span className="tnum font-mono">{formatBytes(size.sizeBytes)}</span>
    if (sizesQuery.isLoading) return <span className="text-text-3">sizing…</span>
    return <span className="text-text-3">—</span>
  }

  return (
    <section>
      <SectionHeader
        title="Volumes"
        description="Named volumes across every stack, plus orphans that no container references."
        action={
          orphans.length > 0 ? (
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setPruneOpen(true)}
              loading={prune.isPending}
            >
              <Trash2 /> Prune orphaned volumes
            </Button>
          ) : undefined
        }
      />

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn’t load volumes"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          {(error as Error)?.message ?? 'An unexpected error occurred.'}
        </Banner>
      ) : isLoading ? (
        <div className="space-y-3">
          <Skeleton variant="rect" className="h-24 w-full" />
          <Skeleton variant="rect" className="h-32 w-full" />
        </div>
      ) : (
        <div className="space-y-5">
          {/* Per-stack volumes, collapsed to counts with a link to each stack's Volumes tab. */}
          <div>
            <h3 className="mb-2 text-xs font-medium uppercase tracking-[0.04em] text-text-3">
              By stack
            </h3>
            {perStack.length === 0 ? (
              <Card>
                <CardContent className="py-6 text-sm text-text-3">
                  No stack-owned volumes yet.
                </CardContent>
              </Card>
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {perStack.map((g) => {
                  const id = stackIdByProject.get(g.project)
                  const inner = (
                    <div className="flex items-center gap-3">
                      <span className="flex size-9 shrink-0 items-center justify-center rounded-md bg-brand-soft text-brand">
                        <Database className="size-[18px]" aria-hidden />
                      </span>
                      <div className="min-w-0">
                        <p className="truncate font-medium text-text">{g.project}</p>
                        <p className="tnum text-[13px] text-text-2">
                          {g.count} {g.count === 1 ? 'volume' : 'volumes'}
                          {g.live > 0 && <> · {g.live} live</>}
                        </p>
                      </div>
                    </div>
                  )
                  return id != null ? (
                    <Link
                      key={g.project}
                      to="/stacks/$id"
                      params={{ id: String(id) }}
                      search={{ tab: 'volumes' }}
                      className="rounded-lg border border-border bg-surface p-4 transition-colors hover:border-border-strong hover:bg-surface-2"
                    >
                      {inner}
                    </Link>
                  ) : (
                    <div
                      key={g.project}
                      className="rounded-lg border border-border bg-surface p-4"
                    >
                      {inner}
                    </div>
                  )
                })}
              </div>
            )}
          </div>

          {/* Orphaned subsection — the triage home (§3.4). Sizes auto-loaded (§3.5). */}
          <div>
            <div className="mb-2 flex items-center gap-2">
              <h3 className="text-xs font-medium uppercase tracking-[0.04em] text-text-3">
                Orphaned
              </h3>
              {orphans.length > 0 && (
                <Badge tone="warn" size="sm">
                  {orphans.length}
                </Badge>
              )}
              {reclaimEstimate > 0 && (
                <span className="tnum text-[13px] text-text-3">
                  ~{formatBytes(reclaimEstimate)} reclaimable
                </span>
              )}
            </div>
            <DataList
              items={orphans}
              getKey={(v) => v.name}
              columns={orphanColumns}
              renderCard={renderOrphanCard}
              emptyState={
                <EmptyState
                  icon={HardDrive}
                  title="No orphaned volumes"
                  description="Every volume is owned by a stack or referenced by a container — nothing to clean up."
                />
              }
              aria-label="Orphaned volumes"
            />
          </div>
        </div>
      )}

      {/* Per-row delete: plain ConfirmDialog, no typed name (orphans hold no stack's data). */}
      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete volume ${pendingDelete.name}?` : 'Delete volume?'}
        description="This volume isn’t used by any container. Deleting it frees its disk space and cannot be undone."
        confirmLabel="Delete"
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => {
          if (pendingDelete) remove.mutate(pendingDelete)
        }}
      />

      {/* Section prune: names the count + est. reclaim from the loaded sizes. */}
      <ConfirmDialog
        open={pruneOpen}
        onOpenChange={(open) => {
          if (!open && !prune.isPending) setPruneOpen(false)
        }}
        title={`Delete all ${orphans.length} orphaned ${orphans.length === 1 ? 'volume' : 'volumes'}?`}
        description={
          reclaimEstimate > 0
            ? `This removes every orphaned volume and frees ~${formatBytes(reclaimEstimate)}. It cannot be undone.`
            : 'This removes every orphaned volume. It cannot be undone.'
        }
        confirmLabel="Prune volumes"
        tone="danger"
        loading={prune.isPending}
        onConfirm={() => prune.mutate()}
      />
    </section>
  )
}

// ── (3) Networks — orphans (read-only, §4.4) + full list with lifecycle chips ──
function NetworksSection({ stackIdByProject }: { stackIdByProject: Map<string, number> }) {
  const { data: networks = [], isLoading, isError, error, refetch } = useQuery({
    queryKey: ['networks', 'fleet'],
    queryFn: () => api.networks.list(null),
    refetchInterval: IDLE_POLL_MS,
  })

  const orphans = useMemo(() => networks.filter((n) => n.lifecycle === 'orphaned'), [networks])
  const rest = useMemo(
    () =>
      [...networks]
        .filter((n) => n.lifecycle !== 'orphaned')
        // Defaults (bridge/host/none) sink to the bottom; then by name.
        .sort(
          (a, b) =>
            Number(a.isDefault) - Number(b.isDefault) || a.name.localeCompare(b.name),
        ),
    [networks],
  )

  function DriverBadge({ n }: { n: NetworkInfo }) {
    if (n.internal) return <Badge tone="warn">internal</Badge>
    return <Badge tone="neutral">{n.driver}</Badge>
  }

  function Subnet({ n }: { n: NetworkInfo }) {
    if (!n.ipam.subnet) return <span className="text-text-3">—</span>
    return (
      <span className="tnum font-mono text-[13px] text-text-2">
        {n.ipam.subnet}
        {n.ipam.gateway ? ` · gw ${n.ipam.gateway}` : ''}
      </span>
    )
  }

  const columns: DataListColumn<NetworkInfo>[] = [
    {
      key: 'name',
      header: 'Network',
      cell: (n) => (
        <span
          className={n.isDefault ? 'font-mono text-[13px] text-text-3' : 'font-mono text-[13px] text-text'}
          title={n.name}
        >
          {n.name}
        </span>
      ),
    },
    {
      key: 'stack',
      header: 'Stack',
      cell: (n) => (
        <StackLink project={n.project} stackIdByProject={stackIdByProject} tab="networks" />
      ),
    },
    {
      key: 'driver',
      header: 'Driver',
      cell: (n) => <DriverBadge n={n} />,
    },
    {
      key: 'subnet',
      header: 'Subnet',
      cell: (n) => <Subnet n={n} />,
    },
    {
      key: 'attached',
      header: 'Attached',
      cell: (n) => (
        <span className="tnum text-[13px] text-text-2">
          {n.attached.length} {n.attached.length === 1 ? 'container' : 'containers'}
        </span>
      ),
    },
    {
      key: 'lifecycle',
      header: 'Status',
      align: 'right',
      cell: (n) => (
        <div className="flex justify-end">
          <LifecycleBadge lifecycle={n.lifecycle} />
        </div>
      ),
    },
  ]

  const renderCard = (n: NetworkInfo) => (
    <div className="space-y-2">
      <div className="flex items-start justify-between gap-3">
        <span
          className={n.isDefault ? 'min-w-0 truncate font-mono text-[13px] text-text-3' : 'min-w-0 truncate font-mono text-[13px] text-text'}
          title={n.name}
        >
          {n.name}
        </span>
        <LifecycleBadge lifecycle={n.lifecycle} />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <DriverBadge n={n} />
        <StackLink project={n.project} stackIdByProject={stackIdByProject} tab="networks" />
      </div>
      <div className="space-y-0.5">
        <Subnet n={n} />
        <p className="tnum text-[13px] text-text-2">
          {n.attached.length} {n.attached.length === 1 ? 'container' : 'containers'} attached
        </p>
      </div>
    </div>
  )

  return (
    <section>
      <SectionHeader
        title="Networks"
        description="Networks across every stack. Networks are recreated on the next deploy — Watchtower doesn’t delete them."
      />

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn’t load networks"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          {(error as Error)?.message ?? 'An unexpected error occurred.'}
        </Banner>
      ) : isLoading ? (
        <div className="space-y-3">
          <Skeleton variant="rect" className="h-24 w-full" />
          <Skeleton variant="rect" className="h-40 w-full" />
        </div>
      ) : (
        <div className="space-y-5">
          {/* Orphaned networks — read-only per §4.4. No delete/prune in v1 (GitOps recreates). */}
          {orphans.length > 0 && (
            <div>
              <div className="mb-2 flex items-center gap-2">
                <h3 className="text-xs font-medium uppercase tracking-[0.04em] text-text-3">
                  Orphaned
                </h3>
                <Badge tone="warn" size="sm">
                  {orphans.length}
                </Badge>
              </div>
              <Banner tone="warn" icon={Network}>
                {orphans.length === 1 ? 'This network isn’t' : 'These networks aren’t'} attached to
                any container. Docker recreates declared networks automatically on the next{' '}
                <span className="font-mono">compose up</span>; remove them manually with{' '}
                <span className="font-mono">docker network rm</span> if intended.
              </Banner>
              <div className="mt-3">
                <DataList
                  items={orphans}
                  getKey={(n) => n.id}
                  columns={columns}
                  renderCard={renderCard}
                  aria-label="Orphaned networks"
                />
              </div>
            </div>
          )}

          {/* Full list with lifecycle chips. */}
          <div>
            <h3 className="mb-2 text-xs font-medium uppercase tracking-[0.04em] text-text-3">
              All networks
            </h3>
            <DataList
              items={rest}
              getKey={(n) => n.id}
              columns={columns}
              renderCard={renderCard}
              emptyState={
                <EmptyState
                  icon={Layers}
                  title="No networks"
                  description="Docker reports no networks on this host beyond the built-in defaults."
                />
              }
              aria-label="All networks"
            />
          </div>
        </div>
      )}
    </section>
  )
}
