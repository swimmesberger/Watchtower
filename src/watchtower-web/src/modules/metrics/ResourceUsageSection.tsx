import { useState, useSyncExternalStore } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import type { StackMetrics } from '@/lib/types'
import { formatBytes } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Meter } from '@/components/ui/meter'
import { SectionHeader } from '@/components/ui/section-header'
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

type ResourceDimension = 'cpu' | 'ram'

/**
 * The per-stack resource ranking (§5.2 + F8). Self-contained: it fetches stack metrics
 * itself and polls every 5s while the tab is visible, pausing when hidden. `metrics.stacks`
 * arrives CPU-sorted; the F8 CPU|RAM toggle re-sorts client-side (StackMetrics carries both).
 * Biggest consumer on top.
 */
export function ResourceUsageSection() {
  // Metrics poll (§5.2 + F6): 5s while the tab is visible, paused on document.hidden.
  const documentVisible = useDocumentVisible()
  const metricsInterval = documentVisible ? 5_000 : (false as const)

  const stackMetricsQuery = useQuery({
    queryKey: ['metrics', 'stacks'],
    queryFn: () => api.metrics.stacks(),
    refetchInterval: metricsInterval,
    refetchIntervalInBackground: false,
  })
  const data = stackMetricsQuery.data?.stacks

  const [dimension, setDimension] = useState<ResourceDimension>('cpu')

  const sorted =
    data == null
      ? []
      : [...data].sort((a, b) =>
          dimension === 'cpu'
            ? b.cpuPercent - a.cpuPercent
            : b.memUsedBytes - a.memUsedBytes,
        )

  const maxMem = sorted.reduce((m, s) => Math.max(m, s.memUsedBytes), 0)

  return (
    <section>
      <SectionHeader
        title="Resource usage"
        action={
          data != null &&
          data.length > 0 && (
            <SegmentedToggle value={dimension} onChange={setDimension} />
          )
        }
      />

      {stackMetricsQuery.isError ? (
        // Container-stats error → in-panel danger Banner + Retry (§5.2/§5.5).
        <Banner
          tone="danger"
          title="Couldn't load resource usage"
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() => stackMetricsQuery.refetch()}
              loading={stackMetricsQuery.isFetching}
            >
              Retry
            </Button>
          }
        >
          Docker may be unreachable. Host metrics above are independent.
        </Banner>
      ) : stackMetricsQuery.isLoading || data == null ? (
        <Card>
          <ul className="divide-y divide-border">
            {Array.from({ length: 3 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 p-4 md:px-5">
                <Skeleton variant="line" className="h-4 w-24 shrink-0" />
                <Skeleton variant="rect" className="h-1.5 flex-1" />
                <Skeleton variant="line" className="h-4 w-12 shrink-0" />
              </li>
            ))}
          </ul>
        </Card>
      ) : sorted.length === 0 ? (
        <Card className="p-6">
          <p className="text-center text-sm text-text-3">No running containers.</p>
        </Card>
      ) : (
        <Card>
          <ul className="divide-y divide-border">
            {sorted.map((stack) => (
              <ResourceRow
                key={stack.stackName}
                stack={stack}
                dimension={dimension}
                maxMem={maxMem}
              />
            ))}
          </ul>
        </Card>
      )}
    </section>
  )
}

/** F8 CPU|RAM segmented control — two minimal ghost buttons; default CPU. */
function SegmentedToggle({
  value,
  onChange,
}: {
  value: ResourceDimension
  onChange: (d: ResourceDimension) => void
}) {
  return (
    <div
      role="group"
      aria-label="Sort by"
      className="inline-flex items-center gap-0.5 rounded-md border border-border bg-surface-2 p-0.5"
    >
      {(['cpu', 'ram'] as const).map((dim) => (
        <button
          key={dim}
          type="button"
          aria-pressed={value === dim}
          onClick={() => onChange(dim)}
          className={cn(
            'rounded px-2.5 py-1 text-xs font-medium transition-colors',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--brand-ring)]',
            value === dim
              ? 'bg-surface text-text shadow-[var(--sh-sm)]'
              : 'text-text-3 hover:text-text-2',
          )}
        >
          {dim === 'cpu' ? 'CPU' : 'RAM'}
        </button>
      ))}
    </div>
  )
}

/**
 * One row of the resource ranking: stack name · Meter (selected dimension) · tnum cpu/mem ·
 * Sparkline. `metrics.stacks` is keyed by compose project only (no numeric stack id), so the
 * row is a static block — the whole-row link into the stack lives on the dashboard's own grid.
 */
function ResourceRow({
  stack,
  dimension,
  maxMem,
}: {
  stack: StackMetrics
  dimension: ResourceDimension
  maxMem: number
}) {
  const cpuText = `${Math.round(stack.cpuPercent)}%`
  const memText = formatBytes(stack.memUsedBytes)

  // Meter + sparkline follow the selected dimension (F8).
  const meterValue = dimension === 'cpu' ? stack.cpuPercent : stack.memUsedBytes
  const meterMax = dimension === 'cpu' ? 100 : maxMem || 1
  const meterTonePicked = dimension === 'cpu' ? undefined : ('brand' as const)

  const sparkData =
    dimension === 'cpu'
      ? stack.history.map((h) => h.cpuPercent)
      : stack.history.map((h) => h.memUsedBytes)
  const sparkNormalize: '0-100' | 'auto' = dimension === 'cpu' ? '0-100' : 'auto'
  const sparkTone = dimension === 'cpu' ? undefined : ('brand' as const)

  return (
    <li className="flex items-center gap-3 p-4 md:px-5">
      <div className="flex min-w-0 flex-[2] items-center gap-2">
        <span className="truncate text-sm font-medium text-text">{stack.stackName}</span>
        <span className="hidden shrink-0 text-xs text-text-3 md:inline">
          {stack.containerCount} container{stack.containerCount === 1 ? '' : 's'}
        </span>
      </div>

      <div className="hidden flex-[2] md:block">
        <Meter
          value={meterValue}
          max={meterMax}
          tone={meterTonePicked}
          aria-label={`${stack.stackName} ${dimension === 'cpu' ? 'CPU' : 'memory'} usage`}
        />
      </div>

      <span className="tnum w-12 shrink-0 text-right text-sm text-text-2">
        {dimension === 'cpu' ? cpuText : memText}
      </span>
      <span className="tnum hidden w-20 shrink-0 text-right text-sm text-text-3 sm:inline">
        {dimension === 'cpu' ? memText : cpuText}
      </span>

      <span className="hidden shrink-0 sm:inline">
        <Sparkline
          data={sparkData}
          normalize={sparkNormalize}
          tone={sparkTone}
          aria-label={`${stack.stackName} trend`}
        />
      </span>
    </li>
  )
}
