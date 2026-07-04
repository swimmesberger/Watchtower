import { Switch as SwitchPrimitive } from 'radix-ui'
import { cn } from '@/lib/utils'

export interface SwitchProps
  extends React.ComponentPropsWithoutRef<typeof SwitchPrimitive.Root> {}

/** On = `--brand`; 20px thumb; --dur-2 transition. */
export function Switch({ className, ...props }: SwitchProps) {
  return (
    <SwitchPrimitive.Root
      className={cn(
        'peer inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-full border border-transparent',
        'transition-colors focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
        'disabled:cursor-not-allowed disabled:opacity-50',
        'data-[state=checked]:bg-brand data-[state=unchecked]:bg-surface-3',
        className,
      )}
      {...props}
    >
      <SwitchPrimitive.Thumb
        className={cn(
          'pointer-events-none block size-5 rounded-full bg-white shadow-[var(--sh-sm)]',
          'transition-transform data-[state=checked]:translate-x-5 data-[state=unchecked]:translate-x-0.5',
        )}
      />
    </SwitchPrimitive.Root>
  )
}
