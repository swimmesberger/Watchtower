import { cva, type VariantProps } from 'class-variance-authority'
import { Slot } from 'radix-ui'
import { cn } from '@/lib/utils'
import { Spinner } from './spinner'

const buttonVariants = cva(
  [
    'inline-flex items-center justify-center gap-2 whitespace-nowrap select-none',
    'rounded-md font-medium transition-colors',
    'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
    'active:translate-y-px disabled:pointer-events-none disabled:opacity-45',
    '[&_svg]:size-4 [&_svg]:shrink-0',
  ],
  {
    variants: {
      variant: {
        primary: 'bg-brand text-brand-fg hover:bg-[var(--brand-hover)] active:bg-[var(--brand-active)]',
        secondary: 'bg-surface text-text border border-border-strong hover:bg-surface-2',
        ghost: 'bg-transparent text-text hover:bg-surface-2',
        danger: 'bg-danger text-danger-fg hover:brightness-110',
        link: 'bg-transparent text-brand underline-offset-4 hover:underline p-0 h-auto',
      },
      size: {
        sm: 'h-[30px] px-3 text-[13px]',
        md: 'h-9 px-4 text-[13px]',
        default: 'h-9 px-4 text-sm',
        icon: 'size-9',
        'icon-sm': 'size-[30px]',
      },
    },
    defaultVariants: { variant: 'primary', size: 'md' },
  },
)

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  /** Render as the child element (Radix Slot) — for links styled as buttons. */
  asChild?: boolean
  /** Swaps the leading icon for a spinner and disables the button. */
  loading?: boolean
}

export function Button({
  className,
  variant,
  size,
  asChild = false,
  loading = false,
  disabled,
  children,
  ...props
}: ButtonProps) {
  // Slot requires a single child element, so the loading spinner is only
  // injected for the native <button> path (asChild is used for plain links).
  if (asChild) {
    return (
      <Slot.Root className={cn(buttonVariants({ variant, size }), className)} {...props}>
        {children}
      </Slot.Root>
    )
  }
  return (
    <button
      className={cn(buttonVariants({ variant, size }), className)}
      disabled={disabled || loading}
      {...props}
    >
      {loading && <Spinner size="sm" label="" aria-hidden />}
      {children}
    </button>
  )
}

export { buttonVariants }
