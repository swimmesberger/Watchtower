import { useMemo, useState, type MouseEvent } from 'react'
import { cn } from '@/lib/utils'

/** One line on the chart. Points are oldest → newest; `v` may be null for a gap. */
export interface ChartSeries {
  label: string
  /** Any CSS color (e.g. `var(--brand)` or `#22c55e`). */
  color: string
  points: { t: number; v: number | null }[]
}

export interface TimeSeriesChartProps {
  series: ChartSeries[]
  /** Fixed y-max (e.g. 100 for percentages). Omit to auto-scale to the data. */
  yMax?: number
  /** Formats a value for the y-axis, legend, and tooltip. */
  format?: (v: number) => string
  height?: number
  'aria-label'?: string
  className?: string
}

// viewBox geometry — the SVG scales to its container width; coordinates are fixed here.
const VBW = 720
const M = { top: 12, right: 14, bottom: 24, left: 46 }

function niceMax(v: number): number {
  if (v <= 0) return 1
  const pow = Math.pow(10, Math.floor(Math.log10(v)))
  const n = v / pow
  const step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10
  return step * pow
}

function fmtTime(ms: number, spanMs: number): string {
  const d = new Date(ms)
  if (spanMs > 2 * 24 * 3600_000)
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' })
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

/**
 * A hand-rolled multi-series time-series line chart (no chart dependency). Fixed viewBox that scales to
 * the container width; y-grid + labels, a few x time ticks, one polyline per series, and a hover
 * crosshair with a tooltip listing each series' value at the nearest sample.
 */
export function TimeSeriesChart({
  series,
  yMax,
  format = (v) => `${Math.round(v)}`,
  height = 240,
  'aria-label': ariaLabel,
  className,
}: TimeSeriesChartProps) {
  const [hoverIdx, setHoverIdx] = useState<number | null>(null)

  const model = useMemo(() => {
    const all = series.flatMap((s) => s.points)
    const ts = [...new Set(all.map((p) => p.t))].sort((a, b) => a - b)
    if (ts.length === 0) return null

    const tMin = ts[0]!
    const tMax = ts[ts.length - 1]!
    const tSpan = tMax - tMin || 1
    const dataMax = Math.max(0, ...all.map((p) => (p.v == null ? 0 : p.v)))
    const top = yMax ?? niceMax(dataMax)

    const plotW = VBW - M.left - M.right
    const plotH = height - M.top - M.bottom
    const x = (t: number) => M.left + ((t - tMin) / tSpan) * plotW
    const y = (v: number) => M.top + plotH - (Math.max(0, Math.min(top, v)) / top) * plotH

    // Per-series lookup for the tooltip (value at an exact timestamp).
    const lookup = series.map((s) => {
      const m = new Map<number, number | null>()
      for (const p of s.points) m.set(p.t, p.v)
      return m
    })

    const yTicks = [0, 0.25, 0.5, 0.75, 1].map((f) => f * top)
    const xTickCount = 5
    const xTicks = Array.from({ length: xTickCount }, (_, i) => tMin + (tSpan * i) / (xTickCount - 1))

    return { ts, tMin, tMax, tSpan, top, plotW, plotH, x, y, lookup, yTicks, xTicks }
  }, [series, yMax, height])

  if (!model) {
    return (
      <div
        className={cn('flex items-center justify-center text-sm text-text-3', className)}
        style={{ height }}
      >
        No data in this range.
      </div>
    )
  }

  const { ts, tSpan, top, x, y, lookup, yTicks, xTicks } = model
  const hoverT = hoverIdx != null ? ts[hoverIdx] : null

  function onMove(e: MouseEvent<SVGRectElement>) {
    const svg = e.currentTarget.ownerSVGElement
    if (!svg) return
    const rect = svg.getBoundingClientRect()
    const xView = ((e.clientX - rect.left) / rect.width) * VBW
    const frac = Math.max(0, Math.min(1, (xView - M.left) / (VBW - M.left - M.right)))
    setHoverIdx(Math.round(frac * (ts.length - 1)))
  }

  return (
    <div className={cn('w-full', className)}>
      <svg
        viewBox={`0 0 ${VBW} ${height}`}
        width="100%"
        height={height}
        role="img"
        aria-label={ariaLabel ?? 'Time series'}
        className="overflow-visible"
      >
        {/* Y grid + labels */}
        {yTicks.map((v, i) => (
          <g key={i}>
            <line
              x1={M.left}
              y1={y(v)}
              x2={VBW - M.right}
              y2={y(v)}
              stroke="var(--border)"
              strokeWidth={1}
              strokeDasharray={i === 0 ? undefined : '3 3'}
            />
            <text x={M.left - 6} y={y(v) + 3} textAnchor="end" className="fill-[var(--text-3)] text-[11px]">
              {format(v)}
            </text>
          </g>
        ))}

        {/* X time labels */}
        {xTicks.map((t, i) => (
          <text
            key={i}
            x={x(t)}
            y={height - 6}
            textAnchor={i === 0 ? 'start' : i === xTicks.length - 1 ? 'end' : 'middle'}
            className="fill-[var(--text-3)] text-[11px]"
          >
            {fmtTime(t, tSpan)}
          </text>
        ))}

        {/* Series polylines (split on null gaps) */}
        {series.map((s) => {
          const segments: string[] = []
          let cur: string[] = []
          for (const p of s.points) {
            if (p.v == null) {
              if (cur.length) segments.push(cur.join(' '))
              cur = []
            } else {
              cur.push(`${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`)
            }
          }
          if (cur.length) segments.push(cur.join(' '))
          return segments.map((pts, i) => (
            <polyline
              key={`${s.label}-${i}`}
              points={pts}
              fill="none"
              stroke={s.color}
              strokeWidth={1.75}
              strokeLinejoin="round"
              strokeLinecap="round"
            />
          ))
        })}

        {/* Hover crosshair + dots */}
        {hoverT != null && (
          <>
            <line
              x1={x(hoverT)}
              y1={M.top}
              x2={x(hoverT)}
              y2={height - M.bottom}
              stroke="var(--text-3)"
              strokeWidth={1}
              strokeDasharray="2 2"
            />
            {series.map((s, si) => {
              const v = lookup[si]?.get(hoverT)
              if (v == null) return null
              return <circle key={s.label} cx={x(hoverT)} cy={y(v)} r={3} fill={s.color} />
            })}
          </>
        )}

        {/* Interaction overlay */}
        <rect
          x={M.left}
          y={M.top}
          width={VBW - M.left - M.right}
          height={height - M.top - M.bottom}
          fill="transparent"
          onMouseMove={onMove}
          onMouseLeave={() => setHoverIdx(null)}
        />
      </svg>

      {/* Legend (+ hovered values) */}
      <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1">
        {series.map((s, si) => {
          const hv = hoverT != null ? lookup[si]?.get(hoverT) : undefined
          const latest = [...s.points].reverse().find((p) => p.v != null)?.v
          const shown = hv != null ? hv : latest
          return (
            <span key={s.label} className="inline-flex items-center gap-1.5 text-xs text-text-2">
              <span className="inline-block size-2.5 rounded-[2px]" style={{ background: s.color }} />
              <span className="text-text">{s.label}</span>
              {shown != null && <span className="tnum text-text-3">{format(shown)}</span>}
            </span>
          )
        })}
        {hoverT != null && (
          <span className="tnum ml-auto text-xs text-text-3">
            {new Date(hoverT).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}
          </span>
        )}
        {/* keep `top` referenced for a11y-free lint calm */}
        <span className="sr-only">max {format(top)}</span>
      </div>
    </div>
  )
}
