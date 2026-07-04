import { useState } from 'react'
import { AlertDialog } from 'radix-ui'
import { cn } from '@/lib/utils'
import { buttonVariants } from './button'
import { Spinner } from './spinner'
import { Input } from './input'

export interface ConfirmDialogProps {
  /** Controlled open state. Omit both to use `trigger`. */
  open?: boolean
  onOpenChange?: (open: boolean) => void
  /** Trigger element (uncontrolled mode). */
  trigger?: React.ReactNode
  title: string
  description?: React.ReactNode
  confirmLabel?: string
  cancelLabel?: string
  tone?: 'danger' | 'brand'
  /** Shows a spinner and disables the confirm button while a mutation is pending. */
  loading?: boolean
  /**
   * When set, the confirm button stays disabled until the user types this exact
   * string into a mono input (A4 — used for stack deletion).
   */
  requireText?: string
  onConfirm: () => void
}

/**
 * Radix AlertDialog confirmation. Replaces every window.confirm().
 * Focus starts on Cancel; Esc / scrim cancels. Confirm shows `loading` until the
 * caller closes the dialog (typically in the mutation's onSettled).
 */
export function ConfirmDialog({
  open,
  onOpenChange,
  trigger,
  title,
  description,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  tone = 'brand',
  loading = false,
  requireText,
  onConfirm,
}: ConfirmDialogProps) {
  const [typed, setTyped] = useState('')
  const gateOpen = requireText ? typed === requireText : true
  const confirmDisabled = loading || !gateOpen

  return (
    <AlertDialog.Root open={open} onOpenChange={onOpenChange}>
      {trigger && <AlertDialog.Trigger asChild>{trigger}</AlertDialog.Trigger>}
      <AlertDialog.Portal>
        <AlertDialog.Overlay className="fixed inset-0 z-50 bg-scrim" />
        <AlertDialog.Content
          onCloseAutoFocus={() => setTyped('')}
          className={cn(
            'fixed z-50 flex flex-col gap-4 bg-overlay text-text shadow-[var(--sh-lg)]',
            'inset-x-0 bottom-0 rounded-t-xl border-t border-border p-5 pb-safe',
            'sm:inset-x-auto sm:bottom-auto sm:left-1/2 sm:top-1/2 sm:w-full sm:max-w-md sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-xl sm:border',
          )}
        >
          <div className="flex flex-col gap-1.5">
            <AlertDialog.Title className="text-lg font-semibold leading-tight">
              {title}
            </AlertDialog.Title>
            {description && (
              <AlertDialog.Description className="text-sm text-text-2">
                {description}
              </AlertDialog.Description>
            )}
          </div>

          {requireText && (
            <div className="flex flex-col gap-1.5">
              <label className="text-xs text-text-2">
                Type <span className="font-mono text-text">{requireText}</span> to confirm
              </label>
              <Input
                mono
                autoComplete="off"
                spellCheck={false}
                value={typed}
                onChange={(e) => setTyped(e.target.value)}
                aria-label={`Type ${requireText} to confirm`}
              />
            </div>
          )}

          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <AlertDialog.Cancel
              className={cn(buttonVariants({ variant: 'secondary' }), 'sm:w-auto')}
              disabled={loading}
            >
              {cancelLabel}
            </AlertDialog.Cancel>
            {/* Not AlertDialog.Action: it force-closes on click, but we keep the
                dialog open while `loading` so the caller controls dismissal. */}
            <button
              type="button"
              disabled={confirmDisabled}
              onClick={onConfirm}
              className={cn(
                buttonVariants({ variant: tone === 'danger' ? 'danger' : 'primary' }),
                'sm:w-auto',
              )}
            >
              {loading && <Spinner size="sm" label="" aria-hidden />}
              {confirmLabel}
            </button>
          </div>
        </AlertDialog.Content>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  )
}
