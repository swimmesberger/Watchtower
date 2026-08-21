// The app shell renders the navigation from `sidebarItems` contributions — it never imports a feature
// module. Adding a destination is a contribution in the owning module; the shell doesn't change.
import { lazy, Suspense } from 'react'
import { Link, Outlet, useRouteContext, useRouterState } from '@tanstack/react-router'
import { Eye, LogOut, ShieldCheck } from 'lucide-react'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { cn } from '@/lib/utils'
import { ACCOUNT_SECURITY_PATH, goToLogin, logout, LOCAL_USER_ID, LOGIN_PATH } from '@/lib/auth'
import { Toaster } from '@/components/ui/toast'
import { ThemeToggle } from '@/components/ui/theme-toggle'
import { Tooltip, TooltipProvider } from '@/components/ui/tooltip'
import { sidebarGroups, sidebarItems, type SidebarItem } from './points'

// Code-split like every route component is: the operator — who never renders this — should not carry the
// portal in the main chunk, and the realm user pays one extra request on a page that is fetching its list
// anyway.
const AppsPage = lazy(() => import('./AppsPage').then((m) => ({ default: m.AppsPage })))

function isActive(currentPath: string, item: SidebarItem): boolean {
  if (item.exact) return currentPath === item.to
  if (item.to === '/') return currentPath === '/'
  return currentPath.startsWith(item.to)
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

/**
 * Link to the account's own security settings. Rendered under the same rule as {@link SignOutButton}: with
 * `Auth:Enabled` off there is no account to protect, and the MFA endpoints answer 404 in that mode, so an
 * entry point would lead nowhere.
 */
export function SecurityLink({ className }: { className?: string }) {
  const { caps } = useRouteContext({ from: '__root__' })
  const user = caps.user
  if (!user.isAuthenticated || user.id === LOCAL_USER_ID) return null

  return (
    <Tooltip label="Account security">
      <Link
        to={ACCOUNT_SECURITY_PATH}
        aria-label="Account security"
        className={cn(
          'touch-target inline-flex size-9 items-center justify-center rounded-md text-text-2 transition-colors hover:bg-surface-2 hover:text-text',
          'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
          className,
        )}
      >
        <ShieldCheck className="size-[18px]" />
      </Link>
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

  // Ungrouped entries first (header-less), then the shell's groups in declared order; `order` on a
  // contribution ranks it within its group only. Empty groups vanish, so a user whose permissions
  // leave a single section still gets a tidy sidebar. The mobile tab bar stays flat — grouping is
  // desktop chrome.
  const sections = [
    { id: 'ungrouped', label: null as string | null, items: items.filter((i) => !i.group) },
    ...sidebarGroups.map((group) => ({
      id: group.id as string,
      label: group.label as string | null,
      items: items.filter((i) => i.group === group.id),
    })),
  ].filter((section) => section.items.length > 0)

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
  //
  // Account security is the one exception, and it has to be: protecting your own credentials is not
  // management, so the page is reachable in every realm — and swallowing it here would make it reachable
  // only by the operator population, which is the opposite of the point.
  if (caps.isFlagEnabled('apps-portal') && currentPath !== ACCOUNT_SECURITY_PATH) {
    return (
      <TooltipProvider delayDuration={200}>
        {/* No fallback: the page's own skeletons are the loading state, and a spinner for the chunk
            followed by skeletons for the data would be two waits rendered as three. */}
        <Suspense fallback={null}>
          <AppsPage caps={caps} />
        </Suspense>
        <Toaster />
      </TooltipProvider>
    )
  }

  // …and when a realm account does reach it, it gets the page on its own: every destination the sidebar
  // would offer is a management screen whose handlers would answer Forbidden.
  if (caps.isFlagEnabled('apps-portal')) {
    return (
      <TooltipProvider delayDuration={200}>
        <div className="mx-auto w-full max-w-[720px] px-4 py-6 md:px-6">
          <Outlet />
        </div>
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
          <nav className="flex flex-1 flex-col overflow-y-auto px-3">
            {sections.map((section) => (
              <div key={section.id} className="flex flex-col gap-0.5 pb-4 last:pb-0">
                {section.label && (
                  <p className="px-3 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-[0.14em] text-text-3">
                    {section.label}
                  </p>
                )}
                {section.items.map((item) => {
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
              </div>
            ))}
          </nav>
          <div className="flex items-center gap-1 border-t border-border px-3 py-3">
            <ThemeToggle />
            <SecurityLink />
            <SignOutButton />
          </div>
        </aside>

        {/* ── Mobile top bar ── */}
        <header className="sticky top-0 z-30 flex h-[var(--header-h)] items-center justify-between border-b border-border bg-surface px-4 md:hidden">
          <Wordmark />
          <div className="flex items-center gap-1">
            <ThemeToggle />
            <SecurityLink />
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
