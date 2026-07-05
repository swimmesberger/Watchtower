import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Database, HardDrive, Trash2 } from 'lucide-react'
import { api } from '@/lib/api'
import type { VolumeInfo, VolumeSize } from '@/lib/types'
import { formatBytes } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { LifecycleBadge } from '@/components/ui/lifecycle-badge'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

// Volumes poll on the 30s idle cadence (A7 idle backoff). There is no deploy/live
// signal on this fleet-wide view, so we sit permanently on the idle interval.
const IDLE_POLL_MS = 30_000

// ── Volumes — per-stack counts + orphan triage ─────────────────────────────────
export function InfraVolumesSection() {
  const qc = useQueryClient()

  // A project→stackId map lets every fleet-wide row link back to its stack's tab.
  // Volumes carry only the compose project name, not the stack id.
  const stacksQuery = useQuery({ queryKey: ['stacks'], queryFn: api.stacks.list })
  const stackIdByProject = useMemo(() => {
    const map = new Map<string, number>()
    for (const s of stacksQuery.data ?? []) map.set(s.composeProjectName, s.id)
    return map
  }, [stacksQuery.data])

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

  function SizeCellInline({ v }: { v: VolumeInfo }) {
    const size = sizeByName.get(v.name)
    if (size) return <span className="tnum font-mono">{formatBytes(size.sizeBytes)}</span>
    if (sizesQuery.isLoading) return <span className="text-text-3">sizing…</span>
    return <span className="text-text-3">—</span>
  }

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
