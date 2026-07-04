import { useState } from 'react'
import { AlertTriangle, CheckCircle2, Info, X, XCircle } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'

export type BannerTone = 'info' | 'warn' | 'ok' | 'danger'

const toneStyles: Record<BannerTone, { wrap: string; icon: string; defaultIcon: LucideIcon }> = {
  info: { wrap: 'bg-run-bg text-text border-run-bd', icon: 'text-run', defaultIcon: Info },
  warn: { wrap: 'bg-warn-bg text-text border-warn-bd', icon: 'text-warn', defaultIcon: AlertTriangle },
  ok: { wrap: 'bg-ok-bg text-text border-ok-bd', icon: 'text-ok', defaultIcon: CheckCircle2 },
  danger: { wrap: 'bg-danger-bg text-text border-danger-bd', icon: 'text-danger', defaultIcon: XCircle },
}

export interface BannerProps {
  tone?: BannerTone
  title?: string
  children?: React.ReactNode
  icon?: LucideIcon
  /** Right-aligned action slot (e.g. a "Retry" or "Review" button/link). */
  action?: React.ReactNode
  /** When true, shows an × that hides the banner (uncontrolled). */
  dismissible?: boolean
  onDismiss?: () => void
  className?: string
}

/** Inline callout for self-update warnings, docker-config notes, deploy status, query errors. */
export function Banner({
  tone = 'info',
  title,
  children,
  icon,
  action,
  dismissible,
  onDismiss,
  className,
}: BannerProps) {
  const [open, setOpen] = useState(true)
  if (!open) return null
  const styles = toneStyles[tone]
  const Icon = icon ?? styles.defaultIcon

  return (
    <div
      role="status"
      aria-live="polite"
      className={cn('flex items-start gap-3 rounded-lg border p-3 text-sm', styles.wrap, className)}
    >
      <Icon className={cn('mt-0.5 size-4 shrink-0', styles.icon)} aria-hidden />
      <div className="min-w-0 flex-1">
        {title && <p className="font-medium leading-snug">{title}</p>}
        {children && <div className={cn('text-text-2', title && 'mt-0.5')}>{children}</div>}
      </div>
      {action && <div className="shrink-0">{action}</div>}
      {dismissible && (
        <button
          type="button"
          onClick={() => {
            setOpen(false)
            onDismiss?.()
          }}
          aria-label="Dismiss"
          className="shrink-0 rounded-sm text-text-3 hover:text-text focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
        >
          <X className="size-4" />
        </button>
      )}
    </div>
  )
}
