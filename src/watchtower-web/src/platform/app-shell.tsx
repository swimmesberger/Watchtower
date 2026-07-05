// The app shell renders the navigation from `sidebarItems` contributions — it never imports a feature
// module. Adding a destination is a contribution in the owning module; the shell doesn't change.
import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { Eye, Moon, Sun } from 'lucide-react'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { cn } from '@/lib/utils'
import { useTheme } from '@/lib/theme'
import { Toaster } from '@/components/ui/toast'
import { TooltipProvider } from '@/components/ui/tooltip'
import { sidebarItems, type SidebarItem } from './points'

function isActive(currentPath: string, item: SidebarItem): boolean {
  if (item.exact) return currentPath === item.to
  if (item.to === '/') return currentPath === '/'
  return currentPath.startsWith(item.to)
}

function ThemeToggle({ className }: { className?: string }) {
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

function Wordmark() {
  return (
    <Link to="/" className="flex items-center gap-2.5">
      <span className="flex size-7 items-center justify-center rounded-md bg-brand-soft">
        <Eye className="size-4 text-brand" />
      </span>
      <span className="text-[15px] font-bold tracking-tight text-text">Watchtower</span>
    </Link>
  )
}

export function AppShell() {
  const currentPath = useRouterState({ select: (s) => s.location.pathname })
  const items = useContributions(sidebarItems)
  const mobileItems = items.filter((i) => i.mobile !== false)

  return (
    <TooltipProvider delayDuration={200}>
      <div className="min-h-dvh md:flex">
        {/* ── Desktop sidebar ── */}
        <aside className="fixed inset-y-0 left-0 z-30 hidden w-[var(--sidebar-w)] flex-col border-r border-border bg-surface md:flex">
          <div className="px-4 py-4">
            <Wordmark />
          </div>
          <nav className="flex flex-1 flex-col gap-0.5 px-3">
            {items.map((item) => {
              const active = isActive(currentPath, item)
              const Icon = item.icon
              const Badge = item.badge
              return (
                <Link
                  key={item.id}
                  to={item.to}
                  aria-current={active ? 'page' : undefined}
                  className={cn(
                    'relative flex h-9 items-center gap-2.5 rounded-md px-3 text-sm font-medium transition-colors',
                    active
                      ? 'bg-brand-soft text-brand before:absolute before:inset-y-1.5 before:left-0 before:w-0.5 before:rounded-full before:bg-brand'
                      : 'text-text-2 hover:bg-surface-2 hover:text-text',
                  )}
                >
                  <Icon className="size-[18px] shrink-0" />
                  <span className="flex-1">{item.label}</span>
                  {Badge && <Badge placement="sidebar" />}
                </Link>
              )
            })}
          </nav>
          <div className="border-t border-border px-3 py-3">
            <ThemeToggle />
          </div>
        </aside>

        {/* ── Mobile top bar ── */}
        <header className="sticky top-0 z-30 flex h-[var(--header-h)] items-center justify-between border-b border-border bg-surface px-4 md:hidden">
          <Wordmark />
          <ThemeToggle />
        </header>

        {/* ── Content column ── */}
        <div className="flex min-w-0 flex-1 flex-col md:pl-[var(--sidebar-w)]">
          <main className="mx-auto w-full max-w-[1200px] flex-1 px-4 pb-bottombar pt-4 md:px-6 md:pb-10 md:pt-6">
            <Outlet />
          </main>
        </div>

        {/* ── Mobile bottom tab bar (items with mobile !== false) ── */}
        <nav
          className="fixed inset-x-0 bottom-0 z-30 flex h-bottombar border-t border-border bg-surface pb-safe shadow-[var(--sh-md)] md:hidden"
          aria-label="Primary"
        >
          {mobileItems.map((item) => {
            const active = isActive(currentPath, item)
            const Icon = item.icon
            const Badge = item.badge
            return (
              <Link
                key={item.id}
                to={item.to}
                aria-current={active ? 'page' : undefined}
                className={cn(
                  'relative flex min-w-[64px] flex-1 flex-col items-center justify-center gap-0.5 pt-1 text-[10px] font-medium transition-colors',
                  active
                    ? 'text-brand before:absolute before:inset-x-3 before:top-0 before:h-0.5 before:rounded-full before:bg-brand'
                    : 'text-text-3',
                )}
              >
                <span className="relative">
                  <Icon className="size-[22px]" />
                  {Badge && <Badge placement="tab" />}
                </span>
                {item.label}
              </Link>
            )
          })}
        </nav>

        <Toaster />
      </div>
    </TooltipProvider>
  )
}
