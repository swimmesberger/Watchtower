import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { Boxes, Container, Eye, Key, LayoutDashboard, Settings } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'

const navItems = [
  { to: '/', label: 'Dashboard', icon: LayoutDashboard, exact: true },
  { to: '/stacks', label: 'Stacks', icon: Boxes, exact: false },
  { to: '/registries', label: 'Registries', icon: Container, exact: true },
  { to: '/credentials', label: 'Credentials', icon: Key, exact: true },
  { to: '/settings', label: 'Settings', icon: Settings, exact: true },
] as const

export function RootLayout() {
  const routerState = useRouterState()
  const currentPath = routerState.location.pathname

  const { data: selfStatus } = useQuery({
    queryKey: ['system', 'self'],
    queryFn: api.system.getSelf,
    staleTime: 5 * 60_000,
    retry: false,
  })
  const hasUpdate = selfStatus?.isOutdated === true

  return (
    <div className="relative z-[2] flex flex-col min-h-screen">
      {/* ── Top navigation bar ── */}
      <header className="sticky top-0 z-50 flex items-center gap-4 px-6 h-14 border-b border-[var(--topbar-border)] bg-[var(--topbar-background)] backdrop-blur-xl">
        {/* Logo */}
        <Link to="/" className="flex items-center gap-2.5 shrink-0">
          <div className="w-7 h-7 flex items-center justify-center rounded-md bg-gradient-to-br from-[var(--primary)] to-[var(--accent-dim)] shadow-[0_0_20px_var(--accent-glow)]">
            <Eye className="size-4 text-[var(--primary-foreground)]" />
          </div>
          <span className="font-bold text-[15px] tracking-tight">
            Watch<span className="text-[var(--primary)]">tower</span>
          </span>
        </Link>

        {/* Nav pills */}
        <nav className="flex items-center gap-0.5 ml-8 bg-[var(--card)] border border-[rgba(255,255,255,0.06)] rounded-xl p-[3px]">
          {navItems.map(({ to, label, icon: Icon, exact }) => {
            const active = exact ? currentPath === to : currentPath.startsWith(to)
            const showDot = to === '/settings' && hasUpdate
            return (
              <Link
                key={to}
                to={to}
                className={cn(
                  'flex items-center gap-1.5 rounded-[10px] px-3.5 py-1.5 text-[13px] font-medium transition-all whitespace-nowrap',
                  active
                    ? 'bg-[var(--accent)] text-[var(--text-primary)] shadow-[0_1px_3px_rgba(0,0,0,0.3)]'
                    : 'text-[var(--text-secondary)] hover:text-[var(--text-primary)] hover:bg-[var(--accent)]',
                )}
              >
                <Icon className={cn('size-[15px]', active ? 'opacity-100' : 'opacity-60')} />
                {label}
                {showDot && (
                  <span
                    aria-label="Update available"
                    className="size-1.5 rounded-full bg-[var(--warning)] shadow-[0_0_6px_var(--warning)] animate-[wt-pulse-dot_2s_ease-in-out_infinite]"
                  />
                )}
              </Link>
            )
          })}
        </nav>

        {/* Right section */}
        <div className="ml-auto flex items-center gap-3">
          <div className="flex items-center gap-2 text-xs text-[var(--text-tertiary)] font-[var(--font-mono)]">
            <span className="size-[7px] rounded-full bg-[var(--success)] shadow-[0_0_8px_rgba(16,185,129,0.4)] animate-[wt-pulse-dot_3s_ease-in-out_infinite]" />
            <span>System nominal</span>
          </div>
        </div>

        {/* Searchlight beam */}
        <div className="absolute top-[55px] left-0 right-0 h-px overflow-hidden z-50">
          <div className="absolute top-0 -left-[30%] w-[30%] h-full bg-gradient-to-r from-transparent via-[var(--primary)] to-transparent opacity-60 animate-[wt-searchlight_6s_ease-in-out_infinite]" />
        </div>
      </header>

      {/* ── Main content ── */}
      <main className="flex-1 overflow-auto">
        <Outlet />
      </main>
    </div>
  )
}
