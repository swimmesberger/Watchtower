import { useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowRight, Info } from 'lucide-react'
import { api } from '@/lib/api'
import type { MetricsRange, StackMetrics } from '@/lib/types'
import { formatBytes } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { TimeSeriesChart, type ChartSeries } from '@/components/ui/time-series-chart'

const METRICS_HISTORY_DOC = '/docs/metrics-history.md'

const RANGES = [
  { id: '1h', label: '1h', seconds: 3600 },
  { id: '6h', label: '6h', seconds: 6 * 3600 },
  { id: '24h', label: '24h', seconds: 24 * 3600 },
  { id: '7d', label: '7d', seconds: 7 * 24 * 3600 },
] as const
type RangeId = (typeof RANGES)[number]['id']

/** Categorical palette for the per-stack lines. */
const STACK_COLORS = [
  'var(--brand)', '#22c55e', '#f59e0b', '#ef4444',
  '#a855f7', '#06b6d4', '#ec4899', '#84cc16',
]

/** Builds a [now-seconds, now] range with a step that targets ~200 points. */
function buildRange(seconds: number): MetricsRange {
  const now = Date.now()
  return {
    from: new Date(now - seconds * 1000).toISOString(),
    to: new Date(now).toISOString(),
    stepSeconds: Math.max(15, Math.round(seconds / 200)),
  }
}

const pct = (v: number) => `${Math.round(v)}%`

/**
 * The metrics history page (ADR-0007). A time-range picker over durable host + per-stack utilization,
 * available only when the InfluxDB backend is active; otherwise an info banner explains how to enable it.
 */
export function MetricsHistoryPage() {
  const [rangeId, setRangeId] = useState<RangeId>('6h')
  const seconds = RANGES.find((r) => r.id === rangeId)!.seconds

  const caps = useQuery({
    queryKey: ['metrics', 'capabilities'],
    queryFn: api.metrics.capabilities,
    staleTime: 5 * 60_000,
  })
  const historyAvailable = caps.data?.historyAvailable ?? false

  const host = useQuery({
    queryKey: ['metrics', 'host', 'history', rangeId],
    queryFn: () => api.metrics.host(buildRange(seconds)),
    enabled: historyAvailable,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  })
  const stacks = useQuery({
    queryKey: ['metrics', 'stacks', 'history', rangeId],
    queryFn: () => api.metrics.stacks(buildRange(seconds)),
    enabled: historyAvailable,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  })

  const hostHistory = host.data?.history ?? []
  const hostSeries: ChartSeries[] = [
    { label: 'CPU', color: 'var(--brand)', points: hostHistory.map((h) => ({ t: Date.parse(h.t), v: h.cpuPercent })) },
    { label: 'RAM', color: '#a855f7', points: hostHistory.map((h) => ({ t: Date.parse(h.t), v: h.memPercent })) },
  ]

  const topStacks = (stacks.data?.stacks ?? []).slice(0, STACK_COLORS.length)
  const stackSeries: ChartSeries[] = topStacks.map((s, i) => ({
    label: s.stackName,
    color: STACK_COLORS[i % STACK_COLORS.length] ?? 'var(--brand)',
    points: s.history.map((h) => ({ t: Date.parse(h.t), v: h.cpuPercent })),
  }))

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-8 px-4 py-6 md:px-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Metrics history</h1>
          <p className="mt-1 text-sm text-text-2">
            Host and per-stack utilization over time — go back to when an incident happened and read the load around it.
          </p>
        </div>
        {historyAvailable && <RangePicker value={rangeId} onChange={setRangeId} />}
      </div>

      {caps.isLoading ? (
        <Card className="p-5">
          <Skeleton variant="line" className="h-4 w-40" />
          <Skeleton variant="rect" className="mt-4 h-[240px] w-full" />
        </Card>
      ) : !historyAvailable ? (
        <Card className="p-4 md:p-5">
          <Banner
            tone="info"
            icon={Info}
            title="History needs the InfluxDB backend"
            action={
              <a
                href={METRICS_HISTORY_DOC}
                className="inline-flex items-center gap-1 text-[13px] font-medium text-brand transition-colors hover:text-[var(--brand-hover)]"
              >
                How to enable
                <ArrowRight className="size-3.5" aria-hidden />
              </a>
            }
          >
            The active metrics backend is{' '}
            <code className="font-mono text-[12px]">{caps.data?.source ?? 'memory'}</code>, which keeps only a
            short in-memory window. Point Watchtower at an InfluxDB an external collector fills to unlock durable
            history. The live Dashboard strip works either way.
          </Banner>
        </Card>
      ) : (
        <>
          <ChartCard
            title="Host CPU &amp; memory"
            subtitle="Percent of total, across all cores / RAM."
            query={host}
            empty={hostHistory.length === 0}
          >
            <TimeSeriesChart series={hostSeries} yMax={100} format={pct} aria-label="Host CPU and memory history" />
          </ChartCard>

          <ChartCard
            title="Per-stack CPU"
            subtitle={`Summed across each stack's containers${topStacks.length ? ` · top ${topStacks.length}` : ''}.`}
            query={stacks}
            empty={topStacks.length === 0}
          >
            <TimeSeriesChart series={stackSeries} format={pct} aria-label="Per-stack CPU history" />
          </ChartCard>

          <StackBreakdown rangeId={rangeId} seconds={seconds} stacks={stacks.data?.stacks ?? []} />

          <p className="text-xs text-text-3">
            Memory shown as {formatBytes(host.data?.memUsedBytes ?? 0)} of {formatBytes(host.data?.memTotalBytes ?? 0)} at
            the latest sample. Disk and per-container history are on the Dashboard.
          </p>
        </>
      )}
    </div>
  )
}

function ChartCard({
  title,
  subtitle,
  action,
  query,
  empty,
  children,
}: {
  title: string
  subtitle: string
  action?: ReactNode
  query: { isError: boolean; isLoading: boolean; isFetching: boolean; refetch: () => void }
  empty: boolean
  children: ReactNode
}) {
  return (
    <section>
      <SectionHeader title={title} action={action} />
      <p className="-mt-1 mb-2 text-xs text-text-3">{subtitle}</p>
      <Card className="p-4 md:p-5">
        {query.isError ? (
          <Banner
            tone="danger"
            title="Couldn't load history"
            action={
              <Button variant="secondary" size="sm" onClick={() => query.refetch()} loading={query.isFetching}>
                Retry
              </Button>
            }
          >
            The InfluxDB query failed. Check the collector and InfluxDB are reachable.
          </Banner>
        ) : query.isLoading ? (
          <Skeleton variant="rect" className="h-[240px] w-full" />
        ) : empty ? (
          <div className="flex h-[240px] items-center justify-center text-sm text-text-3">No data in this range.</div>
        ) : (
          children
        )}
      </Card>
    </section>
  )
}

/** Per-stack drill-down: pick a stack, chart each of its containers' CPU or RAM over the range. */
function StackBreakdown({
  rangeId,
  seconds,
  stacks,
}: {
  rangeId: RangeId
  seconds: number
  stacks: StackMetrics[]
}) {
  const [pick, setPick] = useState<string | null>(null)
  const [dim, setDim] = useState<'cpu' | 'ram'>('cpu')
  const selected = pick ?? stacks[0]?.stackName ?? null

  const q = useQuery({
    queryKey: ['metrics', 'containers', 'history', rangeId, selected],
    queryFn: () => api.metrics.containers(selected, buildRange(seconds)),
    enabled: !!selected,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  })

  if (stacks.length === 0) return null

  const containers = q.data ?? []
  const series: ChartSeries[] = containers.map((c, i) => ({
    label: c.containerName,
    color: STACK_COLORS[i % STACK_COLORS.length] ?? 'var(--brand)',
    points: c.history.map((h) => ({ t: Date.parse(h.t), v: dim === 'cpu' ? h.cpuPercent : h.memUsedBytes })),
  }))

  return (
    <ChartCard
      title="Stack breakdown"
      subtitle={`Which container in ${selected ?? 'the stack'} is using the most ${dim === 'cpu' ? 'CPU' : 'memory'}.`}
      action={
        <div className="flex items-center gap-2">
          <select
            aria-label="Stack"
            value={selected ?? ''}
            onChange={(e) => setPick(e.target.value)}
            className="rounded-md border border-border bg-surface-2 px-2 py-1 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--brand-ring)]"
          >
            {stacks.map((s) => (
              <option key={s.stackName} value={s.stackName}>
                {s.stackName}
              </option>
            ))}
          </select>
          <DimensionToggle value={dim} onChange={setDim} />
        </div>
      }
      query={q}
      empty={containers.length === 0}
    >
      <TimeSeriesChart
        series={series}
        format={dim === 'cpu' ? pct : formatBytes}
        aria-label={`Per-container ${dim === 'cpu' ? 'CPU' : 'memory'} in ${selected ?? 'stack'}`}
      />
    </ChartCard>
  )
}

/** CPU | RAM segmented toggle for the stack breakdown. */
function DimensionToggle({ value, onChange }: { value: 'cpu' | 'ram'; onChange: (d: 'cpu' | 'ram') => void }) {
  return (
    <div
      role="group"
      aria-label="Dimension"
      className="inline-flex items-center gap-0.5 rounded-md border border-border bg-surface-2 p-0.5"
    >
      {(['cpu', 'ram'] as const).map((d) => (
        <button
          key={d}
          type="button"
          aria-pressed={value === d}
          onClick={() => onChange(d)}
          className={cn(
            'rounded px-2.5 py-1 text-xs font-medium transition-colors',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--brand-ring)]',
            value === d ? 'bg-surface text-text shadow-[var(--sh-sm)]' : 'text-text-3 hover:text-text-2',
          )}
        >
          {d === 'cpu' ? 'CPU' : 'RAM'}
        </button>
      ))}
    </div>
  )
}

function RangePicker({ value, onChange }: { value: RangeId; onChange: (r: RangeId) => void }) {
  return (
    <div
      role="group"
      aria-label="Time range"
      className="inline-flex items-center gap-0.5 rounded-md border border-border bg-surface-2 p-0.5"
    >
      {RANGES.map((r) => (
        <button
          key={r.id}
          type="button"
          aria-pressed={value === r.id}
          onClick={() => onChange(r.id)}
          className={cn(
            'rounded px-2.5 py-1 text-xs font-medium transition-colors',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--brand-ring)]',
            value === r.id ? 'bg-surface text-text shadow-[var(--sh-sm)]' : 'text-text-3 hover:text-text-2',
          )}
        >
          {r.label}
        </button>
      ))}
    </div>
  )
}
