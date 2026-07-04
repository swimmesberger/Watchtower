// Theme store: 'light' | 'dark' | 'system'. Persists the choice in localStorage
// ('wt-theme') and toggles the `.dark` class on <html>. The pre-paint boot script
// in index.html applies the initial class before React mounts; this module keeps it
// in sync afterwards and exposes a subscribable hook.
import { useSyncExternalStore } from 'react'

export type Theme = 'light' | 'dark' | 'system'
export type ResolvedTheme = 'light' | 'dark'

const STORAGE_KEY = 'wt-theme'
const listeners = new Set<() => void>()

let current: Theme = readStored()

function readStored(): Theme {
  if (typeof localStorage === 'undefined') return 'system'
  const v = localStorage.getItem(STORAGE_KEY)
  return v === 'light' || v === 'dark' ? v : 'system'
}

function systemPrefersDark(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(prefers-color-scheme: dark)').matches
  )
}

/** The theme actually rendered right now, resolving 'system' against the OS preference. */
export function resolveTheme(theme: Theme = current): ResolvedTheme {
  if (theme === 'system') return systemPrefersDark() ? 'dark' : 'light'
  return theme
}

function apply(theme: Theme) {
  if (typeof document === 'undefined') return
  document.documentElement.classList.toggle('dark', resolveTheme(theme) === 'dark')
}

function emit() {
  for (const l of listeners) l()
}

/** Current stored theme preference ('light' | 'dark' | 'system'). */
export function getTheme(): Theme {
  return current
}

/** Set the theme preference, persist it, and apply the `.dark` class. */
export function setTheme(theme: Theme) {
  current = theme
  try {
    if (theme === 'system') localStorage.removeItem(STORAGE_KEY)
    else localStorage.setItem(STORAGE_KEY, theme)
  } catch {
    /* ignore storage errors (private mode) */
  }
  apply(theme)
  emit()
}

/** Simple light/dark toggle. Resolves 'system' first, then flips. */
export function toggleTheme() {
  setTheme(resolveTheme() === 'dark' ? 'light' : 'dark')
}

// Keep in sync when the OS preference changes while in 'system' mode.
if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
  const mq = window.matchMedia('(prefers-color-scheme: dark)')
  const onChange = () => {
    if (current === 'system') {
      apply('system')
      emit()
    }
  }
  if (typeof mq.addEventListener === 'function') mq.addEventListener('change', onChange)
  else if (typeof mq.addListener === 'function') mq.addListener(onChange)
}

function subscribe(cb: () => void) {
  listeners.add(cb)
  return () => {
    listeners.delete(cb)
  }
}

/**
 * Subscribe to the theme. Returns { theme, resolved, setTheme, toggle }.
 * `theme` is the preference; `resolved` is 'light' | 'dark' after resolving 'system'.
 */
export function useTheme() {
  const theme = useSyncExternalStore(
    subscribe,
    () => current,
    () => 'system' as Theme,
  )
  return {
    theme,
    resolved: resolveTheme(theme),
    setTheme,
    toggle: toggleTheme,
  }
}
