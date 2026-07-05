import { cn } from '@/lib/utils'
import { meterTone } from '@/lib/format'

export type MeterTone = 'ok' | 'warn' | 'danger' | 'brand' | 'neutral'

export interface MeterProps {
  /** Current value. Combined with `max` to compute the fill percentage. */
  value: number
  /** Full-scale value. Defaults to 100 (so `value` is read as a percentage). */
  max?: number
  /**
   * Fill color. Omit to derive from the fill percentage's threshold token
   * (spec §5.4: ok <80, warn ≥80, danger ≥90). Pass a tone to override.
   */
  tone?: MeterTone
  /** Accessible label for the progressbar role. */
  'aria-label'?: string
  className?: string
}

const toneVar: Record<MeterTone, string> = {
  ok: 'var(--ok)',
  warn: 'var(--warn)',
  danger: 'var(--danger)',
  brand: 'var(--brand)',
  neutral: 'var(--text-2)',
}

/**
 * A 6px horizontal bar (spec §5.4): `--surface-2` track, threshold-colored fill,
 * pill-rounded, width animated over `--dur-2`. Used in the Dashboard resource ranking
 * and container mem %. Reduced-motion freezes the width transition via the global rule.
 */
export function Meter({ value, max = 100, tone, 'aria-label': ariaLabel, className }: MeterProps) {
  const pct = max > 0 ? Math.max(0, Math.min(100, (value / max) * 100)) : 0
  const resolvedTone: MeterTone = tone ?? meterTone(pct)

  return (
    <div
      role="progressbar"
      aria-valuenow={Math.round(pct)}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-label={ariaLabel}
      className={cn('h-1.5 w-full overflow-hidden rounded-full bg-surface-2', className)}
    >
      <div
        className="h-full rounded-full transition-[width] duration-[var(--dur-2)] ease-[var(--ease)]"
        style={{ width: `${pct}%`, backgroundColor: toneVar[resolvedTone] }}
      />
    </div>
  )
}
