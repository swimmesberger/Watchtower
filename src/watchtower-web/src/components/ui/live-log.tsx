import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ArrowDown } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface LiveLogProps {
  /** EventSource URL to stream from. When null/undefined the stream is not opened. */
  url?: string | null
  /**
   * Whether the stream should be open. Alias `open` for parity with disclosure UIs.
   * Defaults to true when a `url` is provided.
   */
  active?: boolean
  open?: boolean
  /** Name of a named SSE event that signals completion (deploy stream uses 'done'). */
  doneEvent?: string
  /** Max height of the scroll region (CSS length). Default 20rem. */
  maxHeight?: string | number
  /** Optional label announced when streaming starts (aria). */
  label?: string
  className?: string
}

type Phase = 'connecting' | 'streaming' | 'reconnecting' | 'done'

/**
 * Shared SSE log viewer (A3). Autoscrolls only while pinned to the bottom; shows a
 * "Jump to latest" pill otherwise. Header shows a "● live" chip while streaming and a
 * "reconnecting…" chip on error. aria-live is throttled to start + final status only.
 *
 * Works with both plain `onmessage` streams (container logs) and streams that end with
 * a named event (pass doneEvent="done" for the deploy-history stream).
 */
export function LiveLog({
  url,
  active,
  open,
  doneEvent,
  maxHeight = '20rem',
  label = 'log',
  className,
}: LiveLogProps) {
  const isOpen = (active ?? open ?? true) && !!url
  const [lines, setLines] = useState<string[]>([])
  const [phase, setPhase] = useState<Phase>('connecting')
  const [pinned, setPinned] = useState(true)

  const scrollRef = useRef<HTMLDivElement>(null)
  const esRef = useRef<EventSource | null>(null)
  const pinnedRef = useRef(true)

  // Open / close the stream.
  useEffect(() => {
    if (!isOpen || !url) {
      esRef.current?.close()
      esRef.current = null
      setLines([])
      setPhase('connecting')
      setPinned(true)
      pinnedRef.current = true
      return
    }

    setLines([])
    setPhase('connecting')

    const es = new EventSource(url)
    esRef.current = es

    es.onopen = () => setPhase('streaming')
    es.onmessage = (e) => {
      setPhase('streaming')
      setLines((prev) => [...prev, e.data])
    }
    if (doneEvent) {
      es.addEventListener(doneEvent, () => {
        setPhase('done')
        es.close()
      })
    }
    es.onerror = () => {
      // If the stream already completed, treat close as done; else it's a drop.
      setPhase((p) => (p === 'done' ? 'done' : 'reconnecting'))
      es.close()
    }

    return () => {
      es.close()
    }
  }, [isOpen, url, doneEvent])

  // Autoscroll when pinned to the bottom.
  useLayoutEffect(() => {
    if (pinnedRef.current && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [lines])

  function onScroll() {
    const el = scrollRef.current
    if (!el) return
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24
    pinnedRef.current = atBottom
    setPinned(atBottom)
  }

  function jumpToLatest() {
    const el = scrollRef.current
    if (!el) return
    el.scrollTop = el.scrollHeight
    pinnedRef.current = true
    setPinned(true)
  }

  if (!isOpen) return null

  const streaming = phase === 'streaming' || phase === 'connecting'

  return (
    <div className={cn('relative overflow-hidden rounded-md border border-border', className)}>
      {/* Header */}
      <div className="flex items-center gap-2 border-b border-border bg-surface-2 px-3 py-1.5 text-[11px] font-medium">
        {streaming && (
          <span className="inline-flex items-center gap-1.5 text-run">
            <span className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]" aria-hidden />
            live
          </span>
        )}
        {phase === 'reconnecting' && (
          <span className="inline-flex items-center gap-1.5 text-warn">
            <span className="size-1.5 rounded-full bg-current" aria-hidden />
            reconnecting…
          </span>
        )}
        {phase === 'done' && <span className="text-text-3">stream ended</span>}
      </div>

      {/* Throttled live region: announce start + final status only. */}
      <p className="sr-only" aria-live="polite">
        {phase === 'streaming' && `Streaming ${label} started`}
        {phase === 'done' && `Streaming ${label} ended`}
        {phase === 'reconnecting' && `Streaming ${label} disconnected, reconnecting`}
      </p>

      {/* Log body: dark inset that reads as a terminal in both themes. */}
      <div
        ref={scrollRef}
        onScroll={onScroll}
        style={{ maxHeight }}
        className="overflow-auto bg-term-bg p-3 font-mono text-[12.5px] leading-[1.6] text-term-fg"
      >
        {lines.length === 0 ? (
          <span className="italic text-term-muted">No output yet…</span>
        ) : (
          lines.map((line, i) => <div key={i} className="whitespace-pre-wrap break-words">{line || ' '}</div>)
        )}
      </div>

      {/* Jump-to-latest pill */}
      {!pinned && (
        <button
          type="button"
          onClick={jumpToLatest}
          className="absolute bottom-3 left-1/2 inline-flex -translate-x-1/2 items-center gap-1.5 rounded-full border border-border bg-overlay px-3 py-1 text-xs font-medium text-text shadow-[var(--sh-md)] focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
        >
          Jump to latest
          <ArrowDown className="size-3.5" />
        </button>
      )}
    </div>
  )
}
