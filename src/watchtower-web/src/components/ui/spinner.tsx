import { cn } from '@/lib/utils'

const sizes = {
  sm: 'size-3.5 border-[1.5px]',
  md: 'size-4 border-2',
  lg: 'size-6 border-2',
} as const

export interface SpinnerProps extends React.HTMLAttributes<HTMLSpanElement> {
  size?: keyof typeof sizes
  /** Accessible label; also used as the visually-hidden text. */
  label?: string
}

/** Minimal 1px-track ring spinner in `currentColor`. */
export function Spinner({ size = 'md', label = 'Loading', className, ...props }: SpinnerProps) {
  return (
    <span role="status" aria-live="polite" className={cn('inline-flex', className)} {...props}>
      <span
        className={cn(
          'inline-block animate-spin rounded-full border-current border-t-transparent',
          sizes[size],
        )}
        aria-hidden
      />
      <span className="sr-only">{label}</span>
    </span>
  )
}
