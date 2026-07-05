import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'

// The "update available" dot on the Settings nav item. The dynamic check lives in the settings module
// (the owner), not the shell — the shell just exposes the badge slot on each sidebar item.
export function SettingsUpdateBadge({ placement }: { placement: 'sidebar' | 'tab' }) {
  const { data } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: 5 * 60_000,
    retry: false,
  })
  if (data?.isOutdated !== true) return null

  return (
    <span
      role="img"
      aria-label="Update available"
      className={cn(
        'size-1.5 rounded-full bg-warn',
        placement === 'tab' && 'absolute -right-1 -top-0.5 ring-2 ring-surface',
      )}
    />
  )
}
