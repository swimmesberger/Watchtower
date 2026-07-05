import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Lock, Network } from 'lucide-react'
import { api } from '@/lib/api'
import type { NetworkInfo, PortConflict, PublishedPort, Stack } from '@/lib/types'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { ExposureBadge } from '@/components/ui/exposure-badge'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'

// ── Networks tab (§4.2–§4.3, F10) ────────────────────────────────────────────────

export function StackNetworksTab({ stack }: { stack: Stack }) {
  const project = stack.composeProjectName

  const {
    data: networks = [],
    isLoading: netsLoading,
    isError: netsError,
    refetch: refetchNets,
  } = useQuery({
    queryKey: ['networks', project],
    queryFn: () => api.networks.list(project),
    refetchInterval: 30_000,
  })

  const {
    data: ports,
    isLoading: portsLoading,
  } = useQuery({
    queryKey: ['networks', 'ports', project],
    queryFn: () => api.networks.ports(project),
    refetchInterval: 30_000,
  })

  const published = useMemo(() => {
    const list = ports?.published ?? []
    // Sort by exposure risk: public first, then localhost, then internal-only.
    const rank: Record<string, number> = { public: 0, localhost: 1, none: 2 }
    return [...list].sort((a, b) => (rank[a.exposure] ?? 3) - (rank[b.exposure] ?? 3))
  }, [ports])
  const conflicts = ports?.conflicts ?? []

  if (netsError) {
    return (
      <Banner
        tone="danger"
        title="Couldn’t load networks"
        action={
          <Button variant="secondary" size="sm" onClick={() => refetchNets()}>
            Retry
          </Button>
        }
      >
        The Docker socket may be unreachable.
      </Banner>
    )
  }

  const netColumns: DataListColumn<NetworkInfo>[] = [
    {
      key: 'name',
      header: 'Network',
      cell: (n) => <span className="truncate font-mono text-[12.5px] text-text">{n.name}</span>,
    },
    {
      key: 'compose',
      header: 'Compose name',
      cell: (n) =>
        n.composeNetwork ? (
          <Badge tone="neutral" size="sm">
            {n.composeNetwork}
          </Badge>
        ) : (
          <span className="text-text-3">—</span>
        ),
    },
    {
      key: 'driver',
      header: 'Driver',
      cell: (n) => (
        <div className="flex flex-wrap items-center gap-1.5">
          <Badge tone="neutral" size="sm">
            {n.driver}
          </Badge>
          {n.internal && (
            <Tooltip label="Internal network — no outbound route.">
              <Badge tone="warn" size="sm" tabIndex={0}>
                internal
              </Badge>
            </Tooltip>
          )}
        </div>
      ),
    },
    {
      key: 'ipam',
      header: 'Subnet',
      cell: (n) => (
        <span className="tnum font-mono text-[12px] text-text-2">
          {n.ipam.subnet ?? '—'}
          {n.ipam.gateway && <span className="text-text-3"> · gw {n.ipam.gateway}</span>}
        </span>
      ),
    },
    {
      key: 'attached',
      header: 'Attached',
      cell: (n) =>
        n.attached.length === 0 ? (
          <span className="text-text-3">—</span>
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {n.attached.map((e) => (
              <span
                key={e.containerId}
                className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] text-text-2"
              >
                <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
                <span className="truncate font-mono">{e.containerName}</span>
                {e.ipv4 && <span className="tnum text-text-3">· {e.ipv4}</span>}
              </span>
            ))}
          </div>
        ),
    },
  ]

  return (
    <div className="space-y-8">
      {/* Block A — networks + attachment strip. */}
      <section className="space-y-4">
        <SectionHeader title="Networks" />
        <p className="-mt-2 text-[13px] text-text-2">
          How this stack’s services are wired together on the Docker network.
        </p>
        <DataList
          items={networks}
          columns={netColumns}
          getKey={(n) => n.id}
          skeletonRows={netsLoading ? 2 : undefined}
          aria-label="Networks"
          emptyState={
            <EmptyState
              icon={Network}
              title="No networks"
              description="This stack has no dedicated networks."
            />
          }
          renderCard={(n) => (
            <div className="space-y-2">
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="min-w-0 truncate font-mono text-[13px] text-text">{n.name}</span>
                <Badge tone="neutral" size="sm">
                  {n.driver}
                </Badge>
                {n.internal && (
                  <Badge tone="warn" size="sm">
                    internal
                  </Badge>
                )}
              </div>
              {n.ipam.subnet && (
                <p className="tnum font-mono text-[12px] text-text-2">{n.ipam.subnet}</p>
              )}
              {n.attached.length > 0 && (
                <div className="flex flex-wrap gap-1.5">
                  {n.attached.map((e) => (
                    <span
                      key={e.containerId}
                      className="inline-flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2 py-0.5 text-[11px] text-text-2"
                    >
                      <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
                      <span className="truncate font-mono">{e.containerName}</span>
                      {e.ipv4 && <span className="tnum text-text-3">· {e.ipv4}</span>}
                    </span>
                  ))}
                </div>
              )}
            </div>
          )}
        />

        {networks.length > 0 && <AttachmentStrip networks={networks} />}
      </section>

      {/* Block B — published ports exposure map. */}
      <section className="space-y-4">
        <SectionHeader title="Published ports" />
        <p className="-mt-2 text-[13px] text-text-2">
          What deploying this stack opened to the network.
        </p>

        {conflicts.map((c) => (
          <PortConflictBanner key={`${c.hostIp}:${c.publicPort}/${c.protocol}`} conflict={c} />
        ))}

        {portsLoading ? (
          <div className="space-y-2 rounded-lg border border-border p-4">
            {Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} variant="line" className="h-4 w-2/3" />
            ))}
          </div>
        ) : published.length === 0 ? (
          <Banner tone="info" title="No published ports">
            This stack isn’t reachable from the host network.
          </Banner>
        ) : (
          <ExposureTable ports={published} />
        )}
      </section>
    </div>
  )
}

function PortConflictBanner({ conflict }: { conflict: PortConflict }) {
  return (
    <Banner tone="warn" title="Port conflict">
      Port {conflict.publicPort}/{conflict.protocol} is claimed by {conflict.containerNames.length}{' '}
      containers ({conflict.containerNames.join(', ')}).
    </Banner>
  )
}

function ExposureTable({ ports }: { ports: PublishedPort[] }) {
  const columns: DataListColumn<PublishedPort>[] = [
    {
      key: 'container',
      header: 'Container',
      cell: (p) => <span className="truncate font-mono text-[12.5px] text-text">{p.containerName}</span>,
    },
    {
      key: 'port',
      header: 'Port',
      cell: (p) => (
        <span className="tnum font-mono text-[12.5px] text-text-2">
          {p.privatePort}/{p.protocol}
        </span>
      ),
    },
    {
      key: 'binding',
      header: 'Host binding',
      cell: (p) => (
        <span className="tnum font-mono text-[12px] text-text-2">
          {p.publicPort != null ? `${p.hostIp}:${p.publicPort}` : '—'}
        </span>
      ),
    },
    {
      key: 'exposure',
      header: 'Exposure',
      align: 'right',
      cell: (p) => <ExposureBadge exposure={p.exposure} />,
    },
  ]

  return (
    <DataList
      items={ports}
      columns={columns}
      getKey={(p) => `${p.containerId}:${p.privatePort}/${p.protocol}:${p.hostIp}:${p.publicPort}`}
      aria-label="Published ports"
      renderCard={(p) => (
        <div className="flex items-center justify-between gap-3">
          <div className="min-w-0">
            <p className="truncate font-mono text-[13px] text-text">{p.containerName}</p>
            <p className="tnum font-mono text-[12px] text-text-2">
              {p.privatePort}/{p.protocol}
              {p.publicPort != null && ` · ${p.hostIp}:${p.publicPort}`}
            </p>
          </div>
          <ExposureBadge exposure={p.exposure} />
        </div>
      )}
    />
  )
}

/**
 * Lightweight topology (§4.3): one row per network, containers as dots on a rail linking to a
 * central network pill. Internal networks get a lock glyph; the default bridge is de-emphasized.
 * Collapses to a grouped list on mobile.
 */
function AttachmentStrip({ networks }: { networks: NetworkInfo[] }) {
  const withMembers = networks.filter((n) => n.attached.length > 0)
  if (withMembers.length === 0) return null

  return (
    <div className="space-y-3">
      {withMembers.map((n) => (
        <div
          key={n.id}
          className={cn(
            'flex flex-col gap-3 rounded-lg border border-border bg-surface p-4 md:flex-row md:items-center md:gap-4',
            n.isDefault && 'opacity-80',
          )}
        >
          {/* Network pill */}
          <span
            className={cn(
              'inline-flex w-fit items-center gap-1.5 rounded-full border px-3 py-1 text-[12px] font-medium',
              n.isDefault
                ? 'border-border bg-surface-2 text-text-3'
                : 'border-[var(--brand-soft)] bg-brand-soft text-brand',
            )}
          >
            {n.internal && <Lock className="size-3" aria-hidden />}
            <span className="font-mono">{n.name}</span>
            <span className="text-text-3">({n.driver})</span>
          </span>

          {/* Rail of container dots */}
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 md:border-l md:border-border md:pl-4">
            {n.attached.map((e) => (
              <span key={e.containerId} className="inline-flex items-center gap-1.5 text-[12px] text-text-2">
                <span className="size-1.5 shrink-0 rounded-full bg-ok" aria-hidden />
                <span className="font-mono">{e.containerName}</span>
                {e.ipv4 && <span className="tnum text-text-3">{e.ipv4}</span>}
              </span>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}
