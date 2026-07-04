import { Badge, type BadgeTone } from '@/components/ui/badge'
import { Tooltip } from '@/components/ui/tooltip'

/** The server-derived exposure classification (spec §2.2 / §4.2). */
export type Exposure = 'public' | 'localhost' | 'none'

interface ExposureMeta {
  tone: BadgeTone
  label: string
  tooltip: string
}

const EXPOSURE_META: Record<Exposure, ExposureMeta> = {
  public: {
    tone: 'danger',
    label: 'public',
    tooltip: 'Reachable from any host on the network. Bind to 127.0.0.1 to restrict to this machine.',
  },
  localhost: {
    tone: 'neutral',
    label: 'localhost',
    tooltip: 'Published only on 127.0.0.1 — reachable from this machine, not the outside network.',
  },
  none: {
    tone: 'neutral',
    label: 'internal only',
    tooltip: 'Exposed inside Docker but not published to a host port — not reachable from the host network.',
  },
}

export interface ExposureBadgeProps {
  /** The `exposure` field from `networks.ports` (unknown strings fall back to "internal only"). */
  exposure: string
  size?: 'sm' | 'md'
  className?: string
}

/**
 * Maps a port's `exposure` → tone + label + Tooltip, keeping the public/localhost danger
 * semantics identical wherever the exposure map renders (stack Networks tab + Infrastructure
 * page). `public` is `danger`-toned; everything else is neutral (spec §4.2).
 */
export function ExposureBadge({ exposure, size = 'sm', className }: ExposureBadgeProps) {
  const meta = EXPOSURE_META[exposure as Exposure] ?? EXPOSURE_META.none
  return (
    <Tooltip label={meta.tooltip}>
      <Badge tone={meta.tone} size={size} className={className} tabIndex={0}>
        {meta.label}
      </Badge>
    </Tooltip>
  )
}
