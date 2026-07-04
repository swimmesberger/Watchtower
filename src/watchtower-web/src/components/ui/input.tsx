import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const inputVariants = cva(
  [
    'w-full rounded-md border bg-surface-2 text-text placeholder:text-text-3',
    'transition-colors outline-none',
    'focus-visible:border-brand focus-visible:shadow-[var(--sh-focus)]',
    'disabled:opacity-50 disabled:pointer-events-none',
  ],
  {
    variants: {
      size: {
        sm: 'h-[30px] px-2.5 text-[13px]',
        md: 'h-9 px-3 text-sm',
      },
      invalid: {
        true: 'border-danger focus-visible:border-danger',
        false: 'border-border-strong',
      },
      mono: {
        true: 'font-mono text-[13px]',
        false: '',
      },
    },
    defaultVariants: { size: 'md', invalid: false, mono: false },
  },
)

type InputVariantProps = VariantProps<typeof inputVariants>

export interface InputProps
  extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'size'>,
    InputVariantProps {}

export function Input({ className, size, invalid, mono, ...props }: InputProps) {
  return (
    <input
      className={cn(inputVariants({ size, invalid, mono }), className)}
      aria-invalid={invalid || undefined}
      {...props}
    />
  )
}

export interface TextareaProps
  extends Omit<React.TextareaHTMLAttributes<HTMLTextAreaElement>, 'size'>,
    Pick<InputVariantProps, 'invalid' | 'mono'> {}

export function Textarea({ className, invalid, mono, ...props }: TextareaProps) {
  return (
    <textarea
      className={cn(
        inputVariants({ invalid, mono }),
        'h-auto min-h-[80px] py-2 leading-relaxed',
        className,
      )}
      aria-invalid={invalid || undefined}
      {...props}
    />
  )
}

export { inputVariants }
