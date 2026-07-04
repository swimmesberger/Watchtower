import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const badgeVariants = cva(
  'inline-flex items-center gap-1.5 rounded-full border font-medium whitespace-nowrap',
  {
    variants: {
      tone: {
        neutral: 'bg-neutral-bg text-neutral border-neutral-bd',
        brand: 'bg-brand-soft text-brand border-[var(--brand-soft)]',
        ok: 'bg-ok-bg text-ok border-ok-bd',
        warn: 'bg-warn-bg text-warn border-warn-bd',
        run: 'bg-run-bg text-run border-run-bd',
        queue: 'bg-queue-bg text-queue border-queue-bd',
        danger: 'bg-danger-bg text-danger border-danger-bd',
      },
      size: {
        sm: 'px-1.5 py-0.5 text-[11px]',
        md: 'px-2 py-0.5 text-xs',
      },
    },
    defaultVariants: { tone: 'neutral', size: 'md' },
  },
)

export interface BadgeProps
  extends React.HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

export function Badge({ className, tone, size, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ tone, size }), className)} {...props} />
}

export { badgeVariants }
export type BadgeTone = NonNullable<VariantProps<typeof badgeVariants>['tone']>
