import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Layers, Network } from 'lucide-react'
import { api } from '@/lib/api'
import type { NetworkInfo } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { LifecycleBadge } from '@/components/ui/lifecycle-badge'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { StackLink } from '@/components/ui/stack-link'

// The fleet-wide view has no deploy/live signal, so it sits permanently on the 30s idle cadence.
const IDLE_POLL_MS = 30_000

// ── Networks — orphans (read-only, §4.4) + full list with lifecycle chips ──
export function InfraNetworksSection() {
  // A project→stackId map lets every fleet-wide row link back to its stack's tab.
  const stacksQuery = useQuery({ queryKey: ['stacks'], queryFn: api.stacks.list })
  const stackIdByProject = useMemo(() => {
    const map = new Map<string, number>()
    for (const s of stacksQuery.data ?? []) map.set(s.composeProjectName, s.id)
    return map
  }, [stacksQuery.data])

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
