// "Alert me on this device when a deploy fails": the one control for Web Push. It is per device, not per
// account, because a subscription is: the phone on the Home Screen and the laptop's browser each opt in
// on their own, and each can turn itself off without touching the other.
import { useEffect, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { BellRing, Share } from 'lucide-react'
import {
  enablePush,
  isPushSubscribed,
  pushAvailability,
  unsubscribeFromPush,
} from '@/lib/push'
import { rpc } from '@/lib/rpc-client'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'

/**
 * The switch plus its explanation. `compact` is the More sheet's row; the default is the fuller block the
 * account page renders inside a card.
 */
export function DeviceNotifications({ compact = false, className }: { compact?: boolean; className?: string }) {
  const availability = pushAvailability()
  const [subscribed, setSubscribed] = useState<boolean | null>(null)
  const [permission, setPermission] = useState<NotificationPermission | null>(() =>
    availability === 'available' ? Notification.permission : null,
  )
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (availability !== 'available') return
    let alive = true
    void isPushSubscribed().then((value) => alive && setSubscribed(value))
    return () => {
      alive = false
    }
  }, [availability])

  const test = useMutation({
    mutationFn: () => rpc('notifications.test', {}),
    onSuccess: ({ delivered }) =>
      delivered > 0
        ? toast.success('Test notification sent.')
        : toast.error('No device received it. Turn alerts off and on again on this device.'),
    onError: () => toast.error('Could not send a test notification.'),
  })

  async function onToggle(next: boolean) {
    setBusy(true)
    try {
      if (next) {
        // No await before this: iOS shows the permission prompt only inside the tap's own gesture.
        const result = await enablePush()
        setPermission(result)
        if (result === 'granted') {
          const ok = await isPushSubscribed()
          setSubscribed(ok)
          if (ok) toast.success('Deploy-failure alerts are on for this device.')
          else toast.error('This device could not subscribe. Reload the app and try again.')
        } else if (result === 'denied') {
          toast.error('Notifications are blocked for Watchtower on this device.')
        }
      } else {
        await unsubscribeFromPush()
        setSubscribed(false)
      }
    } catch {
      toast.error('Could not change alerts on this device.')
    } finally {
      setBusy(false)
    }
  }

  const title = 'Deploy-failure alerts'

  if (availability === 'install-first') {
    return (
      <Row compact={compact} className={className} title={title}>
        <span className="inline-flex flex-wrap items-center gap-1">
          On iPhone, add Watchtower to your Home Screen first: tap
          <Share className="inline size-3.5" aria-label="Share" />
          then “Add to Home Screen”, and open it from there.
        </span>
      </Row>
    )
  }

  if (availability === 'unsupported') {
    return (
      <Row compact={compact} className={className} title={title}>
        This browser cannot receive push notifications.
      </Row>
    )
  }

  const denied = permission === 'denied'
  const on = subscribed === true && permission === 'granted'

  return (
    <Row
      compact={compact}
      className={className}
      title={title}
      control={
        <Switch
          checked={on}
          disabled={busy || denied || subscribed === null}
          onCheckedChange={(next) => void onToggle(next)}
          aria-label={title}
        />
      }
    >
      {denied
        ? 'Notifications are blocked. Allow them for Watchtower in this device’s settings, then come back.'
        : 'Get a notification on this device when a deploy fails, even while Watchtower is closed.'}
      {on && !compact && (
        <div className="mt-3">
          <Button variant="secondary" size="sm" onClick={() => test.mutate()} disabled={test.isPending}>
            <BellRing />
            Send a test
          </Button>
        </div>
      )}
    </Row>
  )
}

function Row({
  compact,
  className,
  title,
  control,
  children,
}: {
  compact: boolean
  className?: string
  title: string
  control?: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <div className={cn('flex items-start justify-between gap-3', className)}>
      <div className="min-w-0">
        <p className="text-sm font-semibold text-text">{title}</p>
        <div className={cn('mt-0.5 text-text-2', compact ? 'text-xs' : 'text-[13px]')}>{children}</div>
      </div>
      {control && <div className="shrink-0 pt-0.5">{control}</div>}
    </div>
  )
}
