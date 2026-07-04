import { useState } from 'react'
import { Check, Clipboard } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button, type ButtonProps } from './button'
import { toast } from './use-toast'

export interface CopyButtonProps extends Omit<ButtonProps, 'children' | 'onClick'> {
  /** Text to copy to the clipboard. */
  value: string
  /** Optional visible label next to the icon. */
  label?: string
  /** Show a "Copied to clipboard" toast on success (default true). */
  withToast?: boolean
}

/** Ghost icon button that copies `value`; swaps to a check for 1.5s. */
export function CopyButton({
  value,
  label,
  withToast = true,
  variant = 'ghost',
  size,
  className,
  ...props
}: CopyButtonProps) {
  const [copied, setCopied] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      if (withToast) toast.success('Copied to clipboard')
      setTimeout(() => setCopied(false), 1500)
    } catch {
      toast.error('Copy failed', 'Clipboard is unavailable in this context.')
    }
  }

  return (
    <Button
      type="button"
      variant={variant}
      size={size ?? (label ? 'sm' : 'icon-sm')}
      onClick={copy}
      aria-label={label ? undefined : 'Copy'}
      className={cn('touch-target', className)}
      {...props}
    >
      {copied ? <Check className="text-ok" /> : <Clipboard />}
      {label && <span>{copied ? 'Copied' : label}</span>}
    </Button>
  )
}
