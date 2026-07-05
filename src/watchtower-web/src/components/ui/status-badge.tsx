import { cn } from '@/lib/utils'
import { Badge, type BadgeTone } from './badge'

type Descriptor = { tone: BadgeTone; label: string; pulse: boolean }

/**
 * Normalizes every status value in the app to a tone + label + whether the dot pulses.
 * Accepts:
 *  - stack.lastDeployStatus: 'success' | 'failed' | 'running' | 'queued' | null
 *  - deploy event status:    'queued' | 'running' | 'success' | 'failed'
 *  - container state:        'running' | 'exited' | 'created' | 'paused' | 'restarting' | 'dead' | …
 *  - container health:       'healthy' | 'unhealthy' | 'starting' | null
 */
export function describeStatus(status: string | null | undefined): Descriptor {
  const s = (status ?? '').toLowerCase()
  switch (s) {
    // success-ish
    case 'success':
    case 'healthy':
      return { tone: 'ok', label: s === 'healthy' ? 'healthy' : 'success', pulse: false }
    case 'running':
      return { tone: 'run', label: 'running', pulse: true }
    case 'queued':
      return { tone: 'queue', label: 'queued', pulse: true }
    case 'starting':
    case 'restarting':
      return { tone: 'run', label: s, pulse: true }
    case 'failed':
      return { tone: 'danger', label: 'failed', pulse: false }
    case 'unhealthy':
    case 'dead':
      return { tone: 'danger', label: s, pulse: false }
    case 'exited':
    case 'stopped':
      return { tone: 'neutral', label: s, pulse: false }
    case 'created':
    case 'paused':
      return { tone: 'neutral', label: s, pulse: false }
    case '':
      return { tone: 'neutral', label: 'never deployed', pulse: false }
    default:
      return { tone: 'neutral', label: s, pulse: false }
  }
}

export interface StatusBadgeProps {
  /** Raw status string (any of the app's status vocabularies). */
  status: string | null | undefined
  /** Override the derived label. */
  label?: string
  size?: 'sm' | 'md'
  /**
   * Force the live pulse on/off. By default only genuinely-live states
   * (running / queued / starting / restarting) pulse. Respects reduced-motion.
   */
  pulse?: boolean
  className?: string
}

/** Status pill with a leading dot; dot pulses (wt-live) only for live states. */
export function StatusBadge({ status, label, size = 'md', pulse, className }: StatusBadgeProps) {
  const d = describeStatus(status)
  const text = label ?? d.label
  const isPulsing = pulse ?? d.pulse
  return (
    <Badge tone={d.tone} size={size} className={className}>
      <span
        className={cn(
          'size-1.5 rounded-full bg-current',
          isPulsing && 'motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]',
        )}
        aria-hidden
      />
      <span className="sr-only">Status: </span>
      <span className="inline-block first-letter:capitalize">{text}</span>
    </Badge>
  )
}
