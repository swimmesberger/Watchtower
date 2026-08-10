import { Moon, Sun } from 'lucide-react'
import { cn } from '@/lib/utils'
import { useTheme } from '@/lib/theme'

/**
 * Light/dark switch. Extracted from the app shell so the applications portal — which replaces the shell
 * rather than living inside it — offers the same control rather than a second implementation of it.
 */
export function ThemeToggle({ className }: { className?: string }) {
  const { resolved, toggle } = useTheme()
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={resolved === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
      className={cn(
        'touch-target inline-flex size-9 items-center justify-center rounded-md text-text-2 transition-colors hover:bg-surface-2 hover:text-text',
        'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
        className,
      )}
    >
      {resolved === 'dark' ? <Sun className="size-[18px]" /> : <Moon className="size-[18px]" />}
    </button>
  )
}
