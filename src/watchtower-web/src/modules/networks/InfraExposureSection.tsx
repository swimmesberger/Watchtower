import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ShieldAlert } from 'lucide-react'
import { api } from '@/lib/api'
import type { PublishedPort } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { ExposureBadge } from '@/components/ui/exposure-badge'
import { SectionHeader } from '@/components/ui/section-header'
import { StackLink } from '@/components/ui/stack-link'

// The fleet-wide view has no deploy/live signal, so it sits permanently on the 30s idle cadence.
const IDLE_POLL_MS = 30_000

// Exposure risk ordering: public first, then localhost, then internal-only (spec §4.2).
const EXPOSURE_RANK: Record<string, number> = { public: 0, localhost: 1, none: 2 }

function exposureRank(e: string): number {
  return EXPOSURE_RANK[e] ?? 3
}

function hostBinding(p: PublishedPort): string {
  if (p.publicPort == null) return '—'
  return `${p.hostIp}:${p.publicPort}`
}

// ── Exposure — all published ports across stacks (the security glance) ──────────
export function InfraExposureSection() {
  // A project→stackId map lets every fleet-wide row link back to its stack's tab.
  // Ports carry only the compose project name, not the stack id.
  const stacksQuery = useQuery({ queryKey: ['stacks'], queryFn: api.stacks.list })
  const stackIdByProject = useMemo(() => {
    const map = new Map<string, number>()
    for (const s of stacksQuery.data ?? []) map.set(s.composeProjectName, s.id)
    return map
  }, [stacksQuery.data])

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
