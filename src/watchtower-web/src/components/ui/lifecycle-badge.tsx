import { Badge, type BadgeTone } from './badge'
import type { ResourceLifecycle } from '@/lib/types'

// Shared lifecycle chip (F4): live→ok, declared→neutral, orphaned→warn. Used by the volumes and
// networks views on both the stack-detail tabs and the fleet-wide Infrastructure page.
const LIFECYCLE_META: Record<ResourceLifecycle, { tone: BadgeTone; label: string }> = {
  live: { tone: 'ok', label: 'live' },
  declared: { tone: 'neutral', label: 'declared' },
  orphaned: { tone: 'warn', label: 'orphaned' },
}

export function LifecycleBadge({
  lifecycle,
  size = 'sm',
}: {
  lifecycle: ResourceLifecycle
  size?: 'sm' | 'md'
}) {
  const meta = LIFECYCLE_META[lifecycle]
  return (
    <Badge tone={meta.tone} size={size}>
      {meta.label}
    </Badge>
  )
}
