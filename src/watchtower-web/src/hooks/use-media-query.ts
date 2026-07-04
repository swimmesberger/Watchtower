import { useSyncExternalStore } from 'react'

/**
 * SSR-safe media-query hook. Returns whether `query` currently matches.
 * Example: const isDesktop = useMediaQuery('(min-width: 768px)')
 */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (cb) => {
      if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return () => {}
      const mql = window.matchMedia(query)
      if (typeof mql.addEventListener === 'function') mql.addEventListener('change', cb)
      else mql.addListener(cb)
      return () => {
        if (typeof mql.removeEventListener === 'function') mql.removeEventListener('change', cb)
        else mql.removeListener(cb)
      }
    },
    () =>
      typeof window !== 'undefined' && typeof window.matchMedia === 'function'
        ? window.matchMedia(query).matches
        : false,
    () => false,
  )
}
