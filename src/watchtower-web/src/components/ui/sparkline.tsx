import { cn } from '@/lib/utils'
import { meterTone } from '@/lib/format'

export type SparklineTone = 'ok' | 'warn' | 'danger' | 'brand' | 'neutral'

export interface SparklineProps {
  /**
   * The series to plot, oldest → newest. Percentages (0–100) by default; byte
   * series or any other scale are normalized to their own min/max via `normalize="auto"`.
   */
  data: number[]
  width?: number
  height?: number
  /**
   * Stroke color. Omit to derive from the LAST value's threshold token
   * (spec §5.4: neutral <80, warn ≥80, danger ≥90). Pass an explicit tone to override.
   */
  tone?: SparklineTone
  /**
   * '0-100' (default) plots on a fixed 0–100 axis (percentages);
   * 'auto' normalizes to the series' own min/max (byte series, load, etc.).
   */
  normalize?: '0-100' | 'auto'
  /** Accessible label; defaults to a generic description. */
  'aria-label'?: string
  className?: string
}

const toneVar: Record<SparklineTone, string> = {
  ok: 'var(--ok)',
  warn: 'var(--warn)',
  danger: 'var(--danger)',
  brand: 'var(--brand)',
  neutral: 'var(--text-2)',
}

/**
 * A hand-rolled inline-SVG sparkline (no chart dep). Draws a single polyline over the
 * `history` ring. Color follows the current value's threshold token unless `tone` is set.
 * With fewer than 2 points it renders a flat baseline plus a "collecting…" hint, matching
 * the server's fresh-start state (spec §5.4). Static under reduced-motion (it already is —
 * it simply re-renders on poll).
 */
export function Sparkline({
  data,
  width = 60,
  height = 20,
  tone,
  normalize = '0-100',
  'aria-label': ariaLabel,
  className,
}: SparklineProps) {
  const pad = 1.5 // keep the 1.5px stroke inside the viewBox at the extremes
  const collecting = data.length < 2

  // Resolve stroke tone: explicit prop, else threshold of the latest percentage value.
  const resolvedTone: SparklineTone =
    tone ?? (normalize === '0-100' ? meterTone(data[data.length - 1]) : 'neutral')
  const stroke = toneVar[resolvedTone]

  if (collecting) {
    // Flat baseline centered vertically + a muted caption.
    const midY = height / 2
    return (
      <span className={cn('inline-flex items-center gap-1.5', className)}>
        <svg
          width={width}
          height={height}
          viewBox={`0 0 ${width} ${height}`}
          role="img"
          aria-label={ariaLabel ?? 'No data yet'}
          className="overflow-visible"
        >
          <line
            x1={pad}
            y1={midY}
            x2={width - pad}
            y2={midY}
            stroke="var(--surface-3)"
            strokeWidth={1.5}
            strokeLinecap="round"
          />
        </svg>
        <span className="text-[11px] text-text-3">collecting…</span>
      </span>
    )
  }

  // Compute the y-domain.
  let min: number
  let max: number
  if (normalize === '0-100') {
    min = 0
    max = 100
  } else {
    min = Math.min(...data)
    max = Math.max(...data)
  }
  const span = max - min || 1 // avoid divide-by-zero on a flat series

  const innerW = width - pad * 2
  const innerH = height - pad * 2
  const step = data.length > 1 ? innerW / (data.length - 1) : 0

  const points = data
    .map((v, i) => {
      const x = pad + step * i
      const clamped = Math.max(min, Math.min(max, v))
      const y = pad + innerH - ((clamped - min) / span) * innerH
      return `${x.toFixed(2)},${y.toFixed(2)}`
    })
    .join(' ')

  const baselineY = height - pad

  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={ariaLabel ?? 'Trend'}
      className={cn('overflow-visible', className)}
    >
      {/* Faint baseline */}
      <line
        x1={pad}
        y1={baselineY}
        x2={width - pad}
        y2={baselineY}
        stroke="var(--surface-2)"
        strokeWidth={1}
      />
      <polyline
        points={points}
        fill="none"
        stroke={stroke}
        strokeWidth={1.5}
        strokeLinejoin="round"
        strokeLinecap="round"
      />
    </svg>
  )
}
