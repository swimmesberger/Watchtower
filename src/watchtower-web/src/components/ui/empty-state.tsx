import type { LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface EmptyStateProps {
  icon?: LucideIcon
  title: string
  description?: string
  /** Primary CTA slot (e.g. a Button or Link). */
  action?: React.ReactNode
  /** Optional secondary CTA. */
  secondaryAction?: React.ReactNode
  className?: string
}

/** Centered empty state with an icon-in-tile, title, description and CTA(s). */
export function EmptyState({
  icon: Icon,
  title,
  description,
  action,
  secondaryAction,
  className,
}: EmptyStateProps) {
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center rounded-lg border border-dashed border-border px-6 py-12 text-center',
        className,
      )}
    >
      {Icon && (
        <div className="mb-4 flex size-12 items-center justify-center rounded-lg bg-surface-2">
          <Icon className="size-6 text-text-3" aria-hidden />
        </div>
      )}
      <p className="text-base font-semibold text-text">{title}</p>
      {description && <p className="mt-1 max-w-sm text-[13px] text-text-2">{description}</p>}
      {(action || secondaryAction) && (
        <div className="mt-5 flex flex-wrap items-center justify-center gap-2">
          {action}
          {secondaryAction}
        </div>
      )}
    </div>
  )
}
