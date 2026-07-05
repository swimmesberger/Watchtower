import { useState } from 'react'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { apiBase } from '@/lib/config'
import { LiveLog } from '@/components/ui/live-log'
import { cn } from '@/lib/utils'

interface ContainerLogsProps {
  containerId: string
  containerName: string
  tail?: number
}

/**
 * Collapsible container-log viewer. A thin wrapper over the shared kit `LiveLog`
 * (which owns the EventSource, autoscroll/jump-to-latest, the live/reconnecting
 * chips and the throttled aria-live region). Collapse-on-close semantics are
 * preserved: closing tears down the stream (LiveLog closes its EventSource when
 * `active` goes false), reopening replays from `tail`.
 */
export function ContainerLogs({ containerId, containerName, tail = 100 }: ContainerLogsProps) {
  const [open, setOpen] = useState(false)

  return (
    <div className="overflow-hidden rounded-md border border-border">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className={cn(
          'flex w-full items-center gap-2 bg-surface-2 px-3.5 py-2.5 text-left font-mono text-sm',
          'transition-colors hover:bg-surface-3',
          'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
        )}
      >
        {open ? (
          <ChevronDown className="size-4 shrink-0 text-text-3" />
        ) : (
          <ChevronRight className="size-4 shrink-0 text-text-3" />
        )}
        <span className="flex-1 truncate text-text-2">{containerName}</span>
        <span className="text-[11px] font-medium text-text-3">{open ? 'Hide logs' : 'Logs'}</span>
      </button>

      {open && (
        <LiveLog
          url={`${apiBase}/api/containers/${containerId}/logs?tail=${tail}&follow=true`}
          active={open}
          label={`${containerName} logs`}
          maxHeight="20rem"
          className="rounded-none border-0 border-t"
        />
      )}
    </div>
  )
}
