// The app shell renders the navigation from `sidebarItems` contributions — it never imports a feature
// module. Adding a destination is a contribution in the owning module; the shell doesn't change.
import { Link, Outlet, useRouteContext, useRouterState } from '@tanstack/react-router'
import { Eye, LogOut, Moon, Sun } from 'lucide-react'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { cn } from '@/lib/utils'
import { useTheme } from '@/lib/theme'
import { goToLogin, logout, LOCAL_USER_ID, LOGIN_PATH } from '@/lib/auth'
import { Toaster } from '@/components/ui/toast'
import { Tooltip, TooltipProvider } from '@/components/ui/tooltip'
import { sidebarItems, type SidebarItem } from './points'
import { AppsPage } from './AppsPage'

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

/**
 * Ends the session — globally, since the backend revokes every session the account holds. Rendered only
 * for a real account: with `Auth:Enabled` off the backend reports an implicit local administrator
 * (`LOCAL_USER_ID`) that has nothing to sign out of.
 */
function SignOutButton({ className }: { className?: string }) {
  const { caps } = useRouteContext({ from: '__root__' })
  const user = caps.user
  if (!user.isAuthenticated || user.id === LOCAL_USER_ID) return null

  async function onSignOut() {
    try {
      await logout()
    } finally {
      // Leave regardless: if the call failed the cookie may still be good, and the login page is where
      // the visitor finds that out — staying put with a half-signed-out shell is the worse outcome.
      goToLogin('/')
    }
  }

  return (
    <Tooltip label={`Sign out (${user.id})`}>
      <button
        type="button"
        onClick={() => void onSignOut()}
        aria-label="Sign out"
        className={cn(
          'touch-target inline-flex size-9 items-center justify-center rounded-md text-text-2 transition-colors hover:bg-surface-2 hover:text-text',
          'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
          className,
        )}
      >
        <LogOut className="size-[18px]" />
      </button>
    </Tooltip>
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
  const { caps } = useRouteContext({ from: '__root__' })
  const items = useContributions(sidebarItems)
  const mobileItems = items.filter((i) => i.mobile !== false)

  // The login page is pre-auth: navigation to places the visitor cannot reach yet would be noise, so the
  // shell steps aside and renders the page on its own. (Toasts and tooltips stay — the form uses both.)
  if (currentPath === LOGIN_PATH) {
    return (
      <TooltipProvider delayDuration={200}>
        <Outlet />
        <Toaster />
      </TooltipProvider>
    )
  }

  // A signed-in account outside the operator realm gets the applications portal instead — the whole shell,
  // not just the content column, because every destination in the sidebar is a management screen whose
  // handlers would answer Forbidden (`SystemRealmAuthorizer`). Rendered in place of the route rather than
  // as a redirect, so it is also what they see if they type an admin path in by hand.
  //
  // The `apps-portal` flag is the backend's answer (ADR-0030, resolved from the realm claim), so this
  // never has to derive the realm client-side. It is false for the operator realm, for an unauthenticated
  // boot, and when authentication is switched off — every one of which keeps today's UI exactly.
  if (caps.isFlagEnabled('apps-portal')) {
    return (
      <TooltipProvider delayDuration={200}>
        <AppsPage caps={caps} />
        <Toaster />
      </TooltipProvider>
    )
  }

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
          <div className="flex items-center gap-1 border-t border-border px-3 py-3">
            <ThemeToggle />
            <SignOutButton />
          </div>
        </aside>

        {/* ── Mobile top bar ── */}
        <header className="sticky top-0 z-30 flex h-[var(--header-h)] items-center justify-between border-b border-border bg-surface px-4 md:hidden">
          <Wordmark />
          <div className="flex items-center gap-1">
            <ThemeToggle />
            <SignOutButton />
          </div>
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
