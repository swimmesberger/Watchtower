import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { ArrowDown, Maximize2, Minimize2 } from 'lucide-react'
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
  /** Optional label announced when streaming starts (aria); also the full-screen view's title. */
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
 *
 * The header's expand button takes the same viewer full screen — same stream, same lines, no
 * reconnect. On a phone that is the difference between reading a log and squinting at 18rem of it.
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
  const [fullScreen, setFullScreen] = useState(false)

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

  // Autoscroll when pinned to the bottom — also after switching views, which remounts the scroll region.
  useLayoutEffect(() => {
    if (pinnedRef.current && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [lines, fullScreen])

  // Escape leaves full screen, like closing any other overlay.
  useEffect(() => {
    if (!fullScreen) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setFullScreen(false)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [fullScreen])

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

  const viewer = (
    <div
      className={cn(
        'relative overflow-hidden',
        fullScreen
          ? 'fixed inset-0 z-50 flex flex-col bg-term-bg pt-safe pb-safe'
          : cn('rounded-md border border-border', className),
      )}
      role={fullScreen ? 'dialog' : undefined}
      aria-modal={fullScreen || undefined}
      aria-label={fullScreen ? label : undefined}
    >
      {/* Header */}
      <div className="flex items-center gap-2 border-b border-border bg-surface-2 px-3 py-1.5 text-[11px] font-medium">
        {fullScreen && <span className="truncate text-xs font-semibold text-text">{label}</span>}
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
        <button
          type="button"
          onClick={() => setFullScreen((v) => !v)}
          aria-label={fullScreen ? 'Exit full screen' : 'Full screen'}
          className={cn(
            'touch-target ml-auto inline-flex items-center justify-center rounded text-text-2 hover:text-text',
            'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
            fullScreen ? 'size-8' : 'size-5',
          )}
        >
          {fullScreen ? <Minimize2 className="size-4" /> : <Maximize2 className="size-3.5" />}
        </button>
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
        style={fullScreen ? undefined : { maxHeight }}
        className={cn(
          'overflow-auto bg-term-bg p-3 font-mono text-[12.5px] leading-[1.6] text-term-fg',
          fullScreen && 'min-h-0 flex-1 overscroll-contain',
        )}
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

  // Portalled when full screen: a `fixed` element is only viewport-relative outside any transformed
  // ancestor, and a log can sit inside one (a card, a sheet).
  return fullScreen ? createPortal(viewer, document.body) : viewer
}
