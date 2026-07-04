import { useSyncExternalStore } from 'react'

export type ToastTone = 'success' | 'error' | 'info'

export interface ToastAction {
  label: string
  onClick: () => void
}

export interface ToastItem {
  id: string
  tone: ToastTone
  title: string
  description?: string
  /** ms before auto-dismiss. Defaults: error 7000, others 4500. */
  duration: number
  action?: ToastAction
}

export interface ToastOptions {
  tone?: ToastTone
  title: string
  description?: string
  duration?: number
  action?: ToastAction
}

const MAX = 3
let items: ToastItem[] = []
const listeners = new Set<() => void>()

function emit() {
  for (const l of listeners) l()
}

function subscribe(cb: () => void) {
  listeners.add(cb)
  return () => {
    listeners.delete(cb)
  }
}

function getSnapshot() {
  return items
}

/** Push a toast. Callable from anywhere (module store). Returns the toast id. */
export function toast(opts: ToastOptions): string {
  const tone = opts.tone ?? 'info'
  const id = `t-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`
  const item: ToastItem = {
    id,
    tone,
    title: opts.title,
    description: opts.description,
    duration: opts.duration ?? (tone === 'error' ? 7000 : 4500),
    action: opts.action,
  }
  // newest first; cap at MAX (drop the oldest)
  items = [item, ...items].slice(0, MAX)
  emit()
  return id
}

toast.success = (title: string, description?: string) => toast({ tone: 'success', title, description })
toast.error = (title: string, description?: string) => toast({ tone: 'error', title, description })
toast.info = (title: string, description?: string) => toast({ tone: 'info', title, description })

export function dismissToast(id: string) {
  items = items.filter((t) => t.id !== id)
  emit()
}

/** Subscribe to the toast queue (used by the Toaster viewport). */
export function useToasts(): ToastItem[] {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
}
