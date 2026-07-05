import { useCallback, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, Database, HardDrive, MoreHorizontal, RotateCcw } from 'lucide-react'
import { api } from '@/lib/api'
import type { Stack, VolumeInfo, VolumeSize } from '@/lib/types'
import { absoluteTitle, formatBytes } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { LifecycleBadge } from '@/components/ui/lifecycle-badge'
import { SectionHeader } from '@/components/ui/section-header'
import { toast } from '@/components/ui/use-toast'

// ── Volumes tab (§3.2–§3.5, F4) ─────────────────────────────────────────────────

/** Small StatusBadge-dot + name chip for a container that references a volume. */
function UsedByChip({ name }: { name: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] text-text-2">
      <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
      <span className="truncate font-mono">{name}</span>
    </span>
  )
}

/** A small "● live" chip reused by rows affected by an active deploy/recreate. */
function LiveChip({ label = 'live' }: { label?: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold text-run">
      <span
        className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]"
        aria-hidden
      />
      {label}
    </span>
  )
}

export function StackVolumesTab({ stack }: { stack: Stack }) {
  const qc = useQueryClient()
  const project = stack.composeProjectName

  // A deploy/recreate is in flight while the stack's last deploy is running or queued.
  const isDeploying = stack.lastDeployStatus === 'running' || stack.lastDeployStatus === 'queued'

  const {
    data: volumes = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['volumes', project],
    queryFn: () => api.volumes.list(project),
    // Container backoff (A7): 10s while a deploy is live, else 30s.
    refetchInterval: isDeploying ? 10_000 : 30_000,
  })

  // Lazy sizes (§3.5): fetched once on demand, merged into rows. Never polled.
  const [sizes, setSizes] = useState<Map<string, number> | null>(null)
  const [sizesAt, setSizesAt] = useState<string | null>(null)
  const loadSizes = useMutation({
    mutationFn: () => api.volumes.sizes(project),
    onSuccess: (result: VolumeSize[]) => {
      const map = new Map<string, number>()
      for (const s of result) map.set(s.name, s.sizeBytes)
      setSizes(map)
      setSizesAt(new Date().toISOString())
    },
    onError: (err: Error) =>
      toast({
        tone: 'error',
        title: "Couldn't read volume sizes",
        description: err.message,
        action: { label: 'Retry', onClick: () => loadSizes.mutate() },
      }),
  })

  // Recreate flow state.
  const [recreateOpen, setRecreateOpen] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirmOpen, setConfirmOpen] = useState(false)

  const recreate = useMutation({
    mutationFn: (volumeNames: string[]) => api.volumes.recreate(stack.id, volumeNames),
    onSuccess: () => {
      // The recreate enqueues on the deploy pipeline (§3.3): the deploy banner + a
      // volume-recreate history row take over. Stay on the Volumes tab; refresh stack + events.
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stack.id, 'events'] })
      toast.info(`Recreating volumes for ${stack.name}…`)
    },
    onError: (err: Error) => toast.error('Recreate failed', err.message),
    onSettled: () => {
      setConfirmOpen(false)
      setRecreateOpen(false)
      setSelected(new Set())
    },
  })

  const openRecreate = useCallback((preselect?: string) => {
    setSelected(preselect ? new Set([preselect]) : new Set())
    setConfirmOpen(false)
    setRecreateOpen(true)
  }, [])

  const toggle = useCallback((name: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  }, [])

  const selectedNames = useMemo(() => [...selected], [selected])

  // While a deploy is active, affected rows show a "● live" chip (§3.3 / §6).
  const columns: DataListColumn<VolumeInfo>[] = [
    {
      key: 'name',
      header: 'Volume',
      cell: (v) => (
        <div className="flex items-center gap-2">
          <span className="truncate font-mono text-[12.5px] text-text" title={v.mountpoint}>
            {v.name}
          </span>
          {isDeploying && <LiveChip />}
        </div>
      ),
    },
    {
      key: 'compose',
      header: 'Compose name',
      cell: (v) => (
        <div className="flex items-center gap-1.5">
          {v.composeVolume ? (
            <Badge tone="neutral" size="sm">
              {v.composeVolume}
            </Badge>
          ) : (
            <span className="text-text-3">—</span>
          )}
          {v.driver !== 'local' && (
            <Badge tone="neutral" size="sm">
              {v.driver}
            </Badge>
          )}
        </div>
      ),
    },
    {
      key: 'lifecycle',
      header: 'Status',
      cell: (v) => <LifecycleBadge lifecycle={v.lifecycle} />,
    },
    {
      key: 'usedBy',
      header: 'Used by',
      cell: (v) =>
        v.inUseBy.length === 0 ? (
          <span className="text-text-3">—</span>
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {v.inUseBy.map((n) => (
              <UsedByChip key={n} name={n} />
            ))}
          </div>
        ),
    },
    {
      key: 'size',
      header: 'Size',
      align: 'right',
      cell: (v) => (
        <span className="tnum text-[13px] text-text-2">
          {sizes ? formatBytes(sizes.get(v.name) ?? 0) : '—'}
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (v) => (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${v.name}`}>
              <MoreHorizontal />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent>
            <DropdownMenuItem destructive onSelect={() => openRecreate(v.name)}>
              <RotateCcw /> Recreate…
            </DropdownMenuItem>
            <DropdownMenuItem
              onSelect={() => {
                void navigator.clipboard?.writeText(v.mountpoint)
                toast.success('Copied to clipboard.')
              }}
            >
              <Copy /> Copy mountpoint
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      ),
    },
  ]

  if (isError) {
    return (
      <Banner
        tone="danger"
        title="Couldn’t load volumes"
        action={
          <Button variant="secondary" size="sm" onClick={() => refetch()}>
            Retry
          </Button>
        }
      >
        The Docker socket may be unreachable.
      </Banner>
    )
  }

  return (
    <div className="space-y-4">
      <SectionHeader
        title="Volumes"
        action={
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              size="sm"
              loading={loadSizes.isPending}
              onClick={() => loadSizes.mutate()}
            >
              {!loadSizes.isPending && <HardDrive />}
              {sizes ? 'Refresh sizes' : 'Load sizes'}
            </Button>
            <Button variant="secondary" size="sm" onClick={() => openRecreate()}>
              <RotateCcw /> Recreate volume…
            </Button>
          </div>
        }
      />
      {/* Plain-language lead-in (F10). */}
      <p className="-mt-2 text-[13px] text-text-2">
        Volumes hold this stack’s persistent data — they survive deploys until you recreate them.
      </p>
      {sizesAt && (
        <p className="tnum text-xs text-text-3" title={absoluteTitle(sizesAt)}>
          Sizes as of {new Date(sizesAt).toLocaleTimeString()}
        </p>
      )}

      <DataList
        items={volumes}
        columns={columns}
        getKey={(v) => v.name}
        skeletonRows={isLoading ? 4 : undefined}
        aria-label="Volumes"
        emptyState={
          <EmptyState
            icon={Database}
            title="No volumes"
            description="This stack’s compose file declares no named volumes."
          />
        }
        renderCard={(v) => (
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-2">
              <span className="min-w-0 truncate font-mono text-[13px] text-text">{v.name}</span>
              <div className="flex items-center gap-2">
                {isDeploying && <LiveChip />}
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${v.name}`}>
                      <MoreHorizontal />
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent>
                    <DropdownMenuItem destructive onSelect={() => openRecreate(v.name)}>
                      <RotateCcw /> Recreate…
                    </DropdownMenuItem>
                    <DropdownMenuItem
                      onSelect={() => {
                        void navigator.clipboard?.writeText(v.mountpoint)
                        toast.success('Copied to clipboard.')
                      }}
                    >
                      <Copy /> Copy mountpoint
                    </DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
              </div>
            </div>
            <div className="flex flex-wrap items-center gap-1.5 text-[12px] text-text-2">
              {v.composeVolume && (
                <Badge tone="neutral" size="sm">
                  {v.composeVolume}
                </Badge>
              )}
              <span>· {v.driver}</span>
              <LifecycleBadge lifecycle={v.lifecycle} />
            </div>
            {v.inUseBy.length > 0 && (
              <div className="flex flex-wrap gap-1.5">
                {v.inUseBy.map((n) => (
                  <UsedByChip key={n} name={n} />
                ))}
              </div>
            )}
            <p className="tnum text-[12px] text-text-3">
              Size {sizes ? formatBytes(sizes.get(v.name) ?? 0) : '—'}
            </p>
          </div>
        )}
      />

      {/* Step 1 — select volumes to recreate. */}
      <RecreateSelectDialog
        open={recreateOpen}
        onOpenChange={(o) => {
          setRecreateOpen(o)
          if (!o) setSelected(new Set())
        }}
        stack={stack}
        volumes={volumes}
        sizes={sizes}
        selected={selected}
        onToggle={toggle}
        onContinue={() => {
          setRecreateOpen(false)
          setConfirmOpen(true)
        }}
      />

      {/* Step 2 — typed-name confirm (A4), tone danger, "Wipe & redeploy". */}
      <ConfirmDialog
        open={confirmOpen}
        onOpenChange={(o) => {
          setConfirmOpen(o)
          if (!o) {
            // Backing out of the confirm returns to the selection dialog.
            setRecreateOpen(true)
          }
        }}
        title={`Wipe data for ${stack.name}?`}
        description={
          <span>
            This permanently deletes {selectedNames.length} volume(s) and all their data —
            including any database contents — then redeploys the stack to recreate them empty.{' '}
            <strong>This cannot be undone.</strong>
            <span className="mt-2 flex flex-col gap-0.5">
              {selectedNames.map((n) => (
                <span key={n} className="font-mono text-[12px] text-text">
                  {n}
                </span>
              ))}
            </span>
          </span>
        }
        confirmLabel="Wipe & redeploy"
        tone="danger"
        requireText={stack.name}
        loading={recreate.isPending}
        onConfirm={() => recreate.mutate(selectedNames)}
      />
    </div>
  )
}

/** Step 1 of the recreate flow: a checkbox list of the stack's named volumes (§3.3). */
function RecreateSelectDialog({
  open,
  onOpenChange,
  stack,
  volumes,
  sizes,
  selected,
  onToggle,
  onContinue,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  stack: Stack
  volumes: VolumeInfo[]
  sizes: Map<string, number> | null
  selected: Set<string>
  onToggle: (name: string) => void
  onContinue: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Recreate volumes for {stack.name}</DialogTitle>
          <DialogDescription>
            Choose which named volumes to wipe and recreate empty on the next deploy.
          </DialogDescription>
        </DialogHeader>

        <Banner tone="danger" title="Recreating deletes data permanently">
          Watchtower will stop this stack’s containers, delete the selected volumes, then redeploy
          to recreate them empty. This is how you reset a database to a clean state.
        </Banner>

        <div className="flex max-h-[40dvh] flex-col gap-1 overflow-y-auto">
          {volumes.length === 0 ? (
            <p className="text-sm text-text-3">This stack has no named volumes.</p>
          ) : (
            volumes.map((v) => (
              <label
                key={v.name}
                className="flex cursor-pointer items-center gap-3 rounded-md border border-border px-3 py-2 hover:bg-surface-2"
              >
                <input
                  type="checkbox"
                  checked={selected.has(v.name)}
                  onChange={() => onToggle(v.name)}
                  className="size-4 shrink-0 accent-[var(--brand)]"
                />
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-mono text-[12.5px] text-text">{v.name}</span>
                  <span className="block text-[12px] text-text-2">
                    {v.composeVolume ?? '—'}
                    {v.inUseBy.length > 0 && ` · used by ${v.inUseBy.join(', ')}`}
                    {sizes && ` · ${formatBytes(sizes.get(v.name) ?? 0)}`}
                  </span>
                </span>
              </label>
            ))
          )}
        </div>

        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button variant="danger" disabled={selected.size === 0} onClick={onContinue}>
            Continue
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
