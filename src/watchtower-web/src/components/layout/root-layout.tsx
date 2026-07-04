import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { Boxes, Container, Eye, Key, LayoutDashboard, Moon, Network, Settings, Sun } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useTheme } from '@/lib/theme'
import { Toaster } from '@/components/ui/toast'
import { TooltipProvider } from '@/components/ui/tooltip'

interface NavItem {
  to: string
  label: string
  icon: LucideIcon
  exact: boolean
}

// Desktop sidebar: 6 items — Infrastructure (fleet-wide volumes/networks triage) sits
// directly under Stacks (spec §1). The mobile bottom bar stays hard-capped at 5 (A1),
// so Infrastructure is desktop-only and reachable on mobile via a Dashboard link.
const desktopNavItems: NavItem[] = [
  { to: '/', label: 'Home', icon: LayoutDashboard, exact: true },
  { to: '/stacks', label: 'Stacks', icon: Boxes, exact: false },
  { to: '/infrastructure', label: 'Infrastructure', icon: Network, exact: true },
  { to: '/registries', label: 'Registries', icon: Container, exact: true },
  { to: '/credentials', label: 'Credentials', icon: Key, exact: true },
  { to: '/settings', label: 'Settings', icon: Settings, exact: true },
]

const mobileNavItems: NavItem[] = [
  { to: '/', label: 'Home', icon: LayoutDashboard, exact: true },
  { to: '/stacks', label: 'Stacks', icon: Boxes, exact: false },
  { to: '/registries', label: 'Registries', icon: Container, exact: true },
  { to: '/credentials', label: 'Credentials', icon: Key, exact: true },
  { to: '/settings', label: 'Settings', icon: Settings, exact: true },
]

function isActive(currentPath: string, item: NavItem): boolean {
  return item.exact ? currentPath === item.to : currentPath.startsWith(item.to)
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

export function RootLayout() {
  const currentPath = useRouterState({ select: (s) => s.location.pathname })

  const { data: selfStatus } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: 5 * 60_000,
    retry: false,
  })
  const hasUpdate = selfStatus?.isOutdated === true

  return (
    <TooltipProvider delayDuration={200}>
      <div className="min-h-dvh md:flex">
        {/* ── Desktop sidebar ── */}
        <aside className="fixed inset-y-0 left-0 z-30 hidden w-[var(--sidebar-w)] flex-col border-r border-border bg-surface md:flex">
          <div className="px-4 py-4">
            <Wordmark />
          </div>
          <nav className="flex flex-1 flex-col gap-0.5 px-3">
            {desktopNavItems.map((item) => {
              const active = isActive(currentPath, item)
              const Icon = item.icon
              const showDot = item.to === '/settings' && hasUpdate
              return (
                <Link
                  key={item.to}
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
                  {showDot && (
                    <span
                      aria-label="Update available"
                      role="img"
                      className="size-1.5 rounded-full bg-warn"
                    />
                  )}
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

        {/* ── Mobile bottom tab bar (all 5 destinations, no More sheet — A1) ── */}
        <nav
          className="fixed inset-x-0 bottom-0 z-30 flex h-bottombar border-t border-border bg-surface pb-safe shadow-[var(--sh-md)] md:hidden"
          aria-label="Primary"
        >
          {mobileNavItems.map((item) => {
            const active = isActive(currentPath, item)
            const Icon = item.icon
            const showDot = item.to === '/settings' && hasUpdate
            return (
              <Link
                key={item.to}
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
                  {showDot && (
                    <span
                      aria-label="Update available"
                      role="img"
                      className="absolute -right-1 -top-0.5 size-1.5 rounded-full bg-warn ring-2 ring-surface"
                    />
                  )}
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
