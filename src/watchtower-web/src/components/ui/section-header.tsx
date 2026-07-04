import { cn } from '@/lib/utils'

export interface SectionHeaderProps {
  /** Small uppercase eyebrow above the title. */
  eyebrow?: string
  title: string
  description?: string
  /** Right-aligned action slot. */
  action?: React.ReactNode
  className?: string
}

/** h2 title (+ optional eyebrow/description) with a hairline underline and mb-4. */
export function SectionHeader({ eyebrow, title, description, action, className }: SectionHeaderProps) {
  return (
    <div className={cn('mb-4 flex items-end justify-between gap-4 border-b border-border pb-3', className)}>
      <div className="min-w-0">
        {eyebrow && (
          <p className="mb-1 text-xs font-medium uppercase tracking-[0.04em] text-text-3">
            {eyebrow}
          </p>
        )}
        <h2 className="text-[17px] font-semibold leading-tight text-text">{title}</h2>
        {description && <p className="mt-1 text-[13px] text-text-2">{description}</p>}
      </div>
      {action && <div className="shrink-0">{action}</div>}
    </div>
  )
}
