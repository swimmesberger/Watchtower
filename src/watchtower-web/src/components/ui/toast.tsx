import { Toast as ToastPrimitive } from 'radix-ui'
import { AlertCircle, CheckCircle2, Info, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { dismissToast, useToasts, type ToastItem, type ToastTone } from './use-toast'

const toneIcon: Record<ToastTone, React.ReactNode> = {
  success: <CheckCircle2 className="size-4 text-ok" />,
  error: <AlertCircle className="size-4 text-danger" />,
  info: <Info className="size-4 text-run" />,
}

const toneAccent: Record<ToastTone, string> = {
  success: 'border-l-ok',
  error: 'border-l-danger',
  info: 'border-l-run',
}

function ToastCard({ item }: { item: ToastItem }) {
  return (
    <ToastPrimitive.Root
      duration={item.duration}
      onOpenChange={(open) => {
        if (!open) dismissToast(item.id)
      }}
      className={cn(
        'pointer-events-auto flex items-start gap-3 rounded-lg border border-border border-l-2 bg-overlay p-3 pr-8 text-text shadow-[var(--sh-lg)]',
        'relative w-[calc(100vw-2rem)] sm:w-80',
        'data-[swipe=move]:translate-x-[var(--radix-toast-swipe-move-x)] data-[swipe=cancel]:translate-x-0',
        toneAccent[item.tone],
      )}
      role={item.tone === 'error' ? 'alert' : 'status'}
    >
      <span className="mt-0.5 shrink-0">{toneIcon[item.tone]}</span>
      <div className="flex-1 min-w-0">
        <ToastPrimitive.Title className="text-sm font-medium leading-snug">
          {item.title}
        </ToastPrimitive.Title>
        {item.description && (
          <ToastPrimitive.Description className="mt-0.5 text-[13px] text-text-2 break-words">
            {item.description}
          </ToastPrimitive.Description>
        )}
        {item.action && (
          <ToastPrimitive.Action
            altText={item.action.label}
            onClick={item.action.onClick}
            className="mt-2 inline-flex text-[13px] font-medium text-brand hover:underline"
          >
            {item.action.label}
          </ToastPrimitive.Action>
        )}
      </div>
      <ToastPrimitive.Close
        className="absolute right-2 top-2 rounded-sm text-text-3 hover:text-text focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
        aria-label="Dismiss"
      >
        <X className="size-4" />
      </ToastPrimitive.Close>
    </ToastPrimitive.Root>
  )
}

/**
 * Mounts the Radix Toast provider + viewport. Place once near the app root.
 * Viewport: bottom-right on desktop; top-center on mobile (below the 56px top bar)
 * so the bottom tab bar / sticky actions are never covered.
 */
export function Toaster() {
  const toasts = useToasts()
  return (
    <ToastPrimitive.Provider swipeDirection="right">
      {toasts.map((t) => (
        <ToastCard key={t.id} item={t} />
      ))}
      <ToastPrimitive.Viewport
        className={cn(
          'fixed z-[100] flex max-h-screen flex-col gap-2 outline-none',
          // mobile: top-center below the 56px top bar
          'left-1/2 top-[calc(var(--header-h)+0.5rem)] w-full max-w-[calc(100vw-2rem)] -translate-x-1/2 items-center',
          // desktop: bottom-right
          'md:left-auto md:right-4 md:top-auto md:bottom-4 md:w-80 md:translate-x-0 md:items-end',
        )}
      />
    </ToastPrimitive.Provider>
  )
}
