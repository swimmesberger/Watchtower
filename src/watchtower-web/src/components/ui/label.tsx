import { Label as LabelPrimitive } from 'radix-ui'
import { cn } from '@/lib/utils'

export interface LabelProps extends React.ComponentPropsWithoutRef<typeof LabelPrimitive.Root> {
  /** Appends a `--danger` asterisk. */
  required?: boolean
  /** Optional short hint rendered inline after the label, muted. */
  hint?: string
}

export function Label({ className, required, hint, children, ...props }: LabelProps) {
  return (
    <LabelPrimitive.Root
      className={cn(
        'flex items-center gap-1.5 text-xs font-medium uppercase tracking-[0.04em] text-text-2',
        className,
      )}
      {...props}
    >
      <span className="inline-flex items-center gap-0.5">
        {children}
        {required && (
          <span className="text-danger" aria-hidden>
            *
          </span>
        )}
      </span>
      {hint && <span className="font-normal normal-case tracking-normal text-text-3">{hint}</span>}
    </LabelPrimitive.Root>
  )
}
