import { useSyncExternalStore } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import type { Container } from '@/lib/types'
import { formatBytes, meterTone } from '@/lib/format'
import { Meter } from '@/components/ui/meter'
import { Skeleton } from '@/components/ui/skeleton'
import { Sparkline } from '@/components/ui/sparkline'

/**
 * Subscribes to document visibility (F6/A7): the metrics poll runs at 5s while the
 * tab is visible and pauses entirely when hidden. SSR-safe (defaults to visible).
 */
function useDocumentVisible(): boolean {
  return useSyncExternalStore(
    (onChange) => {
      document.addEventListener('visibilitychange', onChange)
      return () => document.removeEventListener('visibilitychange', onChange)
    },
    () => !document.hidden,
    () => true,
  )
}

/**
 * Compact CPU% + Sparkline + mem row rendered inside each container card (§5.3). Self-contained:
 * it fetches its stack's container metrics itself. Because every container card in a given stack
 * uses the SAME query key (`['metrics','containers', container.stackName]`), React Query dedupes
 * these to a single request per stack — this replaces the old single parent query. Polls 5s while
 * the tab is visible, pausing when hidden. Renders "— · stopped" when online=false.
 */
export function ContainerMetricsRow({ container }: { container: Container }) {
  const documentVisible = useDocumentVisible()
  const project = container.stackName

  const { data: containerMetrics = [] } = useQuery({
    queryKey: ['metrics', 'containers', project],
    queryFn: () => api.metrics.containers(project),
    enabled: !!project && documentVisible,
    refetchInterval: documentVisible ? 5_000 : false,
    refetchIntervalInBackground: false,
  })

  // Match this container's metrics by id; fall back to the (slash-normalized) name so lookups
  // line up with the card's stripped name, mirroring the old parent-query behavior.
  const normalizedName = container.names[0]?.replace(/^\//, '') ?? ''
  const metrics = containerMetrics.find(
    (m) =>
      m.containerId === container.id ||
      m.containerName.replace(/^\//, '') === normalizedName,
  )

  const online = container.state === 'running'

  if (!online || metrics?.online === false) {
    return (
      <div className="mt-3 flex items-center gap-2 border-t border-border pt-3 text-[13px] text-text-3">
        <span className="tnum">—</span>
        <span>stopped</span>
      </div>
    )
  }

  if (!metrics) {
    // Metrics not yet loaded (first poll): a thin skeleton line, never a spinner (§5.5).
    return (
      <div className="mt-3 border-t border-border pt-3">
        <Skeleton variant="line" className="h-4 w-2/3" />
      </div>
    )
  }

  const cpuHistory = metrics.history.map((h) => h.cpuPercent)
  const memPct = metrics.memPercent
  const memTone = meterTone(memPct)

  return (
    <div className="mt-3 flex flex-col gap-3 border-t border-border pt-3 sm:flex-row sm:items-center sm:gap-6">
      {/* CPU */}
      <div className="flex items-center gap-2">
        <span className="text-xs uppercase tracking-[0.04em] text-text-3">CPU</span>
        <span className="tnum text-[13px] font-medium text-text">
          {metrics.cpuPercent.toFixed(0)}%
        </span>
        <Sparkline
          data={cpuHistory}
          width={48}
          height={16}
          aria-label="CPU trend"
          className="shrink-0"
        />
      </div>

      {/* Memory */}
      <div className="flex min-w-0 flex-1 items-center gap-2">
        <span className="text-xs uppercase tracking-[0.04em] text-text-3">RAM</span>
        <span className="tnum whitespace-nowrap text-[13px] text-text-2">
          {formatBytes(metrics.memUsedBytes)}
          {metrics.memLimitBytes != null && (
            <>
              {' / '}
              {formatBytes(metrics.memLimitBytes)}
            </>
          )}
          {memPct != null && (
            <span className="ml-1 text-text-3">({memPct.toFixed(0)}%)</span>
          )}
        </span>
        {memPct != null && (
          <Meter
            value={memPct}
            tone={memTone}
            aria-label="Memory usage"
            className="max-w-[120px]"
          />
        )}
      </div>
    </div>
  )
}
