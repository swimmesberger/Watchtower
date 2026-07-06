import { useSyncExternalStore } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { ArrowRight, Info } from 'lucide-react'
import { api } from '@/lib/api'
import { formatBytes, meterTone } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Sparkline } from '@/components/ui/sparkline'
import { Tooltip } from '@/components/ui/tooltip'

/** Path to the host-metrics setup doc (spec §7); a plain anchor to the repo doc. */
const HOST_METRICS_DOC = '/docs/host-metrics.md'

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
 * The host-health strip (§5.1) — the first dashboard section. Self-contained: it fetches
 * host metrics itself and polls every 5s while the tab is visible, pausing when hidden.
 * Desktop 4-up (CPU · RAM · Load · Disk), mobile 2×2. Degrades to an info Banner when host
 * /proc isn't mounted, while the Disk cell may still render from docker-df.
 */
export function HostHealthSection() {
  // Metrics poll (§5.1 + F6): 5s while the tab is visible, paused on document.hidden.
  const documentVisible = useDocumentVisible()
  const metricsInterval = documentVisible ? 5_000 : (false as const)

  const hostMetricsQuery = useQuery({
    queryKey: ['metrics', 'host'],
    queryFn: () => api.metrics.host(),
    refetchInterval: metricsInterval,
    refetchIntervalInBackground: false,
  })

  const host = hostMetricsQuery.data

  // Query error → persistent in-panel danger Banner + Retry (§5.5).
  if (hostMetricsQuery.isError) {
    return (
      <Card className="p-4 md:p-5">
        <Banner
          tone="danger"
          title="Couldn't load host metrics"
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() => hostMetricsQuery.refetch()}
              loading={hostMetricsQuery.isFetching}
            >
              Retry
            </Button>
          }
        >
          The host-health strip couldn't be loaded. Container metrics may still work below.
        </Banner>
      </Card>
    )
  }

  // Loading → 4 skeleton cells (§5.5).
  if (hostMetricsQuery.isLoading || !host) {
    return (
      <Card className="p-4 md:p-5">
        <div className="grid grid-cols-2 gap-x-6 gap-y-5 md:grid-cols-4 md:divide-x md:divide-border">
          {Array.from({ length: 4 }).map((_, i) => (
            <div key={i} className="md:px-5 md:first:pl-0 md:last:pr-0">
              <Skeleton variant="line" className="h-3 w-12" />
              <Skeleton variant="line" className="mt-3 h-7 w-16" />
              <Skeleton variant="line" className="mt-2 h-3 w-14" />
            </div>
          ))}
        </div>
      </Card>
    )
  }

  const cpuLabel = host.cpuCores != null ? `${host.cpuCores} cores` : undefined
  const loadWarn =
    host.loadAvg1 != null && host.cpuCores != null && host.loadAvg1 > host.cpuCores
  const cpuHistory = host.history.map((h) => h.cpuPercent ?? 0)
  const memHistory = host.history.map((h) => h.memPercent ?? 0)

  // Disk can still populate from docker-df even when host /proc is absent (§5.1).
  const diskFromDockerDf = host.diskSource === 'docker-df'
  const diskAvailable = host.diskPercent != null || host.diskUsedBytes != null

  // Degraded: host /proc not mounted → CPU/RAM/Load collapse to an info Banner; the Disk
  // cell may still render (docker-df fallback).
  if (!host.available) {
    return (
      <Card className="p-4 md:p-5">
        <Banner
          tone="info"
          icon={Info}
          title="Host metrics unavailable"
          action={
            <a
              href={HOST_METRICS_DOC}
              className="inline-flex items-center gap-1 text-[13px] font-medium text-brand transition-colors hover:text-[var(--brand-hover)]"
            >
              Enable host metrics
              <ArrowRight className="size-3.5" aria-hidden />
            </a>
          }
        >
          Watchtower can't read the host's CPU and memory because{' '}
          <code className="font-mono text-[12px]">/proc</code> isn't mounted into its
          container. Container metrics still work.
        </Banner>

        {diskAvailable && (
          <div className="mt-4 border-t border-border pt-4 md:w-1/2 md:pr-5">
            <HostCell
              label="Disk"
              percent={host.diskPercent}
              value={
                host.diskPercent != null
                  ? `${Math.round(host.diskPercent)}%`
                  : host.diskUsedBytes != null
                    ? formatBytes(host.diskUsedBytes)
                    : '—'
              }
              sub={
                host.diskUsedBytes != null && host.diskTotalBytes != null
                  ? `${formatBytes(host.diskUsedBytes)} / ${formatBytes(host.diskTotalBytes)}`
                  : undefined
              }
              valueTooltip={
                diskFromDockerDf
                  ? "Docker's view of disk, not the full host."
                  : undefined
              }
              note={diskFromDockerDf ? 'docker-df' : undefined}
            />
          </div>
        )}

        <InfraFooterLink />
      </Card>
    )
  }

  return (
    <Card className="p-4 md:p-5">
      <div className="grid grid-cols-2 gap-x-6 gap-y-5 md:grid-cols-4 md:divide-x md:divide-border">
        {/* CPU */}
        <HostCell
          className="md:px-5 md:first:pl-0"
          label="CPU"
          percent={host.cpuPercent}
          value={host.cpuPercent != null ? `${Math.round(host.cpuPercent)}%` : '—'}
          sub={cpuLabel}
          spark={cpuHistory}
        />
        {/* RAM */}
        <HostCell
          className="md:px-5"
          label="RAM"
          percent={host.memPercent}
          value={host.memPercent != null ? `${Math.round(host.memPercent)}%` : '—'}
          sub={
            host.memUsedBytes != null && host.memTotalBytes != null
              ? `${formatBytes(host.memUsedBytes)} / ${formatBytes(host.memTotalBytes)}`
              : undefined
          }
          spark={memHistory}
        />
        {/* Load — warn when load1 > cores (§5.1). */}
        <HostCell
          className="md:px-5"
          label="Load"
          value={host.loadAvg1 != null ? host.loadAvg1.toFixed(2) : '—'}
          tone={loadWarn ? 'warn' : undefined}
          sub={host.loadAvg5 != null ? `5m ${host.loadAvg5.toFixed(2)}` : undefined}
          valueTooltip={
            host.cpuCores != null
              ? `1-min load average · ${host.cpuCores} cores`
              : '1-min load average'
          }
        />
        {/* Disk — HostSample carries no disk history, so this cell shows value + %, no
            sparkline; the docker-df provenance rides a Tooltip on the value (§5.1). */}
        <HostCell
          className="md:px-5 md:last:pr-0"
          label="Disk"
          percent={host.diskPercent}
          value={
            host.diskPercent != null
              ? `${Math.round(host.diskPercent)}%`
              : host.diskUsedBytes != null
                ? formatBytes(host.diskUsedBytes)
                : '—'
          }
          sub={
            host.diskUsedBytes != null && host.diskTotalBytes != null
              ? `${formatBytes(host.diskUsedBytes)} / ${formatBytes(host.diskTotalBytes)}`
              : undefined
          }
          valueTooltip={
            diskFromDockerDf ? "Docker's view of disk, not the full host." : undefined
          }
          note={diskFromDockerDf ? 'docker-df' : undefined}
        />
      </div>

      <InfraFooterLink />
    </Card>
  )
}

/** Footer link into the fleet-wide Infrastructure view (§1 mobile path, §5.1). */
function InfraFooterLink() {
  return (
    <div className="mt-4 border-t border-border pt-3">
      <Link
        to="/infrastructure"
        className="inline-flex items-center gap-1 text-[13px] font-medium text-text-2 transition-colors hover:text-brand"
      >
        View all volumes &amp; networks
        <ArrowRight className="size-3.5" aria-hidden />
      </Link>
    </div>
  )
}

/**
 * One cell of the host-health strip: xs uppercase label · big tnum value (threshold-colored) ·
 * optional sub-line · optional Sparkline. Thresholds via `meterTone` unless `tone` is given
 * (Load uses a fixed warn from load>cores).
 */
function HostCell({
  label,
  value,
  sub,
  percent,
  spark,
  tone,
  valueTooltip,
  note,
  className,
}: {
  label: string
  value: string
  sub?: string
  percent?: number | null
  spark?: number[]
  tone?: 'warn' | 'danger'
  /** Tooltip attached to the value (e.g. Load average detail, docker-df provenance). */
  valueTooltip?: string
  /** Small caption under the sub-line (e.g. the "docker-df" disk source). */
  note?: string
  className?: string
}) {
  const resolved = tone ?? (percent != null ? meterTone(percent) : 'ok')
  const valueColor =
    resolved === 'danger' ? 'text-danger' : resolved === 'warn' ? 'text-warn' : 'text-text'

  const valueEl = (
    <span
      className={cn('tnum text-2xl font-semibold leading-none tracking-tight', valueColor)}
    >
      {value}
    </span>
  )

  return (
    <div className={className}>
      <p className="text-[11px] font-medium uppercase tracking-wide text-text-3">{label}</p>
      <div className="mt-2 flex items-end gap-2.5">
        {valueTooltip ? (
          <Tooltip label={valueTooltip}>
            <button
              type="button"
              className="cursor-default rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--brand-ring)]"
            >
              {valueEl}
            </button>
          </Tooltip>
        ) : (
          valueEl
        )}
        {spark != null && (
          <Sparkline data={spark} tone={tone} aria-label={`${label} trend`} />
        )}
      </div>
      {sub && <p className="tnum mt-1 text-xs text-text-2">{sub}</p>}
      {note && <p className="mt-1 text-[11px] text-text-3">{note}</p>}
    </div>
  )
}
