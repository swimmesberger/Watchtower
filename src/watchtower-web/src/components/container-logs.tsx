import { useEffect, useRef, useState } from 'react'
import { ChevronDown, ChevronRight, Loader2, X } from 'lucide-react'
import { apiBase } from '@/lib/config'
import { cn } from '@/lib/utils'

interface ContainerLogsProps {
  containerId: string
  containerName: string
  tail?: number
}

/**
 * Streams container logs via Server-Sent Events from the Docker Engine API proxy.
 * Automatically scrolls to the bottom as new lines arrive.
 */
export function ContainerLogs({ containerId, containerName, tail = 100 }: ContainerLogsProps) {
  const [open, setOpen] = useState(false)
  const [lines, setLines] = useState<string[]>([])
  const [connected, setConnected] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const bottomRef = useRef<HTMLDivElement>(null)
  const esRef = useRef<EventSource | null>(null)

  useEffect(() => {
    if (!open) {
      esRef.current?.close()
      esRef.current = null
      setLines([])
      setConnected(false)
      setError(null)
      return
    }

    const es = new EventSource(
      `${apiBase}/api/containers/${containerId}/logs?tail=${tail}&follow=true`,
    )
    esRef.current = es

    es.onopen = () => setConnected(true)
    es.onmessage = e => {
      setLines(prev => [...prev, e.data])
    }
    es.onerror = () => {
      setError('Connection lost – refresh to reconnect.')
      setConnected(false)
      es.close()
    }

    return () => {
      es.close()
    }
  }, [open, containerId, tail])

  // Auto-scroll to bottom
  useEffect(() => {
    if (open) bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [lines, open])

  return (
    <div className="border border-[rgba(255,255,255,0.06)] rounded-[10px] overflow-hidden">
      {/* Header */}
      <button
        onClick={() => setOpen(o => !o)}
        className="flex w-full items-center gap-2 px-3.5 py-2.5 bg-[var(--card)] text-sm font-[var(--font-mono)] text-left hover:bg-[var(--accent)] transition-colors"
      >
        {open ? <ChevronDown className="size-4 shrink-0 text-[var(--text-tertiary)]" /> : <ChevronRight className="size-4 shrink-0 text-[var(--text-tertiary)]" />}
        <span className="flex-1 truncate text-[var(--text-secondary)]">{containerName}</span>
        {open && connected && (
          <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold text-[var(--success)]">
            <span className="size-1.5 rounded-full bg-[var(--success)] inline-block animate-[wt-pulse-dot_2s_ease-in-out_infinite]" aria-hidden /> live
          </span>
        )}
        {open && !connected && !error && (
          <Loader2 className="size-3.5 animate-spin text-[var(--text-tertiary)]" />
        )}
      </button>

      {/* Log output */}
      {open && (
        <div className="relative bg-[rgba(0,0,0,0.3)] text-[var(--text-secondary)]">
          {error && (
            <div className="flex items-center gap-2 bg-[var(--danger-bg)] text-[var(--danger)] text-xs px-3.5 py-2">
              <X className="size-3.5 shrink-0" />
              {error}
            </div>
          )}
          <div
            className={cn(
              'overflow-auto font-[var(--font-mono)] text-[11px] leading-5 p-3.5',
              'max-h-80',
            )}
          >
            {lines.length === 0 ? (
              <span className="text-[var(--text-tertiary)] italic">No output yet…</span>
            ) : (
              lines.map((line, i) => (
                <div key={i}>{line || '\u00A0'}</div>
              ))
            )}
            <div ref={bottomRef} />
          </div>
        </div>
      )}
    </div>
  )
}
