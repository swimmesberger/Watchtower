// The landing page for a signed-in visitor who is not in the operator realm: the applications they may
// enter, and nothing else. Platform rather than a feature module, and deliberately not a route — the shell
// renders it *instead of* the management UI (see `app-shell.tsx`), so there is no navigation to gate and no
// capability module behind it. Realm accounts have none.
import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { AppWindow, ArrowUpRight, Eye, ShieldCheck } from 'lucide-react'
import { listApps, type AppLink } from '@/lib/apps'
import { ACCOUNT_SECURITY_PATH, goToLogin, logout, LOCAL_USER_ID } from '@/lib/auth'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { ThemeToggle } from '@/components/ui/theme-toggle'
import type { SessionCapabilities } from '@/generated/session-client'

export function AppsPage({ caps }: { caps: SessionCapabilities }) {
  const {
    data: apps = [],
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['access-apps'],
    queryFn: listApps,
  })

  return (
    <div className="min-h-dvh">
      <header className="flex h-[var(--header-h)] items-center justify-between border-b border-border bg-surface px-4 md:px-6">
        <span className="flex items-center gap-2.5">
          <span className="flex size-7 items-center justify-center rounded-md bg-brand-soft">
            <Eye className="size-4 text-brand" />
          </span>
          <span className="text-[15px] font-bold tracking-tight text-text">Watchtower</span>
        </span>
        <div className="flex items-center gap-1">
          <ThemeToggle />
          <SecurityLink caps={caps} />
          <SignOutButton caps={caps} />
        </div>
      </header>

      <main className="mx-auto w-full max-w-[1200px] px-4 py-8 md:px-6 md:py-10">
        <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">
          Your applications
        </h1>
        <p className="mt-1 text-sm text-text-2">
          Everything your account can open. Choose one to be signed in to it.
        </p>

        <div className="mt-6">
          {isError ? (
            <Banner
              tone="danger"
              title="Couldn't load your applications"
              action={
                <Button variant="link" onClick={() => refetch()}>
                  Retry
                </Button>
              }
            >
              Something went wrong while fetching the list.
            </Banner>
          ) : isLoading ? (
            <AppGrid>
              {[0, 1, 2].map((i) => (
                <li key={i}>
                  <Card className="p-4 md:p-5">
                    <Skeleton variant="line" className="w-1/2" />
                    <Skeleton variant="line" className="mt-3 w-3/4" />
                  </Card>
                </li>
              ))}
            </AppGrid>
          ) : apps.length === 0 ? (
            <EmptyState
              icon={AppWindow}
              title="No applications yet"
              description="Nothing has been shared with your account. An administrator can give you access."
            />
          ) : (
            <AppGrid>
              {apps.map((app) => (
                <AppCard key={app.domain} app={app} />
              ))}
            </AppGrid>
          )}
        </div>
      </main>
    </div>
  )
}

function AppGrid({ children }: { children: React.ReactNode }) {
  return <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">{children}</ul>
}

/**
 * A plain `<a>`, and it has to be: opening an application is a full document load onto another origin,
 * where the verify redirect and the silent-SSO hand-over do the actual signing in. Client-side routing has
 * nothing to offer here. The href is the backend's `url` rather than a scheme assembled here, so a
 * plain-HTTP route is linked as one.
 */
function AppCard({ app }: { app: AppLink }) {
  return (
    <li>
      <a
        href={app.url}
        className="block rounded-lg focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
      >
        <Card interactive className="p-4 md:p-5">
          <div className="flex items-start justify-between gap-3">
            <span className="min-w-0 flex-1">
              <span className="block truncate font-medium text-text">{app.name}</span>
              <span className="mt-1 block truncate font-mono text-[13px] text-text-2">{app.domain}</span>
            </span>
            <ArrowUpRight className="size-4 shrink-0 text-text-3" aria-hidden />
          </div>
        </Card>
      </a>
    </li>
  )
}

/**
 * The portal's one link out of itself. Realm accounts see no management UI at all, so without this the
 * account-security page would exist for them and be unreachable. Hidden for the implicit local
 * administrator, under the same rule as {@link SignOutButton}: with `Auth:Enabled` off there is no account
 * to protect and the MFA endpoints answer 404.
 */
function SecurityLink({ caps }: { caps: SessionCapabilities }) {
  const user = caps.user
  if (!user.isAuthenticated || user.id === LOCAL_USER_ID) return null

  return (
    <Button variant="ghost" size="sm" asChild>
      <Link to={ACCOUNT_SECURITY_PATH}>
        <ShieldCheck />
        Security
      </Link>
    </Button>
  )
}

/**
 * Ends the session — globally, since the backend revokes every session the account holds. Rendered only
 * for a real account: with `Auth:Enabled` off the backend reports an implicit local administrator
 * (`LOCAL_USER_ID`) that has nothing to sign out of.
 */
function SignOutButton({ caps }: { caps: SessionCapabilities }) {
  const user = caps.user
  if (!user.isAuthenticated || user.id === LOCAL_USER_ID) return null

  async function onSignOut() {
    try {
      await logout()
    } finally {
      goToLogin('/')
    }
  }

  return (
    <Button variant="secondary" size="sm" onClick={() => void onSignOut()}>
      Sign out
    </Button>
  )
}
