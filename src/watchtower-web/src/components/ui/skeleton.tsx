import { cn } from '@/lib/utils'

export interface SkeletonProps extends React.HTMLAttributes<HTMLDivElement> {
  variant?: 'rect' | 'line' | 'circle'
}

/** Shimmer placeholder. Freezes to a static fill under prefers-reduced-motion. */
export function Skeleton({ variant = 'rect', className, ...props }: SkeletonProps) {
  return (
    <div
      aria-hidden
      className={cn(
        'wt-skeleton',
        variant === 'rect' && 'rounded-md',
        variant === 'line' && 'h-3.5 rounded',
        variant === 'circle' && 'rounded-full',
        className,
      )}
      {...props}
    />
  )
}
