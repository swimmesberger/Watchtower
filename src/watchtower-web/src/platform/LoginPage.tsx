import { useEffect, useState, type FormEvent } from 'react'
import { Eye } from 'lucide-react'
import {
  AccessDeniedError,
  continueSession,
  login,
  LoginError,
  safeRedirectTarget,
} from '@/lib/auth'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/use-toast'
import { loginRoute } from './login-route'

/**
 * Where the cross-domain hand-over stands. `handing-over` is a terminal state as far as this component is
 * concerned — it ends in a document load, not a re-render.
 */
type Handover = 'none' | 'handing-over' | 'denied'

export function LoginPage() {
  const { redirect, redirect_uri: redirectUri } = loginRoute.useSearch()
  const { caps } = loginRoute.useRouteContext()
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // A signed-in visitor arriving with `redirect_uri` never sees the form: the round trip starts on mount,
  // so the initial state has to say so or the form would flash first.
  const [handover, setHandover] = useState<Handover>(
    redirectUri && caps.user.isAuthenticated ? 'handing-over' : 'none',
  )

  // Silent SSO. The central session already exists; all that is missing is one for the app's own domain,
  // and the backend answers with the URL that mints it.
  useEffect(() => {
    if (!redirectUri || !caps.user.isAuthenticated) return

    let cancelled = false
    continueSession(redirectUri)
      .then((continueUrl) => {
        if (!cancelled) window.location.assign(continueUrl)
      })
      .catch((failure: unknown) => {
        if (cancelled) return
        if (failure instanceof AccessDeniedError) {
          setHandover('denied')
          setError(failure.message)
          return
        }
        // Anything else — chiefly a central session that expired between boot and now — means the
        // credentials form is the right next step after all.
        setHandover('none')
      })

    return () => {
      cancelled = true
    }
  }, [redirectUri, caps.user.isAuthenticated])

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (busy) return

    setBusy(true)
    setError(null)
    try {
      const result = await login(userName, password, redirectUri)
      // A full document load rather than a router navigation: the capability snapshot and the contribution
      // registry are both resolved once per boot in `main.tsx`, so this is what re-runs that bootstrap
      // against the new identity instead of carrying the anonymous one across the sign-in boundary. For
      // the cross-domain case it is not a choice at all — the target is another origin.
      window.location.assign(result.continueUrl ?? safeRedirectTarget(redirect))
    } catch (failure) {
      setBusy(false)
      if (failure instanceof AccessDeniedError) {
        setHandover('denied')
        setError(failure.message)
      } else if (failure instanceof LoginError) {
        setError(failure.message)
      } else {
        const message = failure instanceof Error ? failure.message : 'Sign-in failed.'
        setError(message)
        toast.error('Could not reach Watchtower.', message)
      }
    }
  }

  return (
    <div className="flex min-h-dvh items-center justify-center px-4 py-10">
      <div className="w-full max-w-[360px]">
        <div className="mb-6 flex flex-col items-center gap-3">
          <span className="flex size-10 items-center justify-center rounded-lg bg-brand-soft">
            <Eye className="size-5 text-brand" />
          </span>
          <h1 className="text-lg font-bold tracking-tight text-text">
            {handover === 'denied' ? 'Access denied' : 'Sign in to Watchtower'}
          </h1>
        </div>

        {/* The requested application is deliberately not named: `redirect_uri` is attacker-reachable, so
            it is passed to the backend and otherwise never surfaces in the page. */}
        {handover !== 'none' ? (
          <Card>
            <CardContent>
              <p className="text-sm text-text-2">
                {handover === 'denied'
                  ? (error ?? 'You are not permitted to access that application.')
                  : 'Signing you in to the requested application…'}
              </p>
            </CardContent>
          </Card>
        ) : (
        <Card>
          <CardContent>
            <form onSubmit={onSubmit} className="flex flex-col gap-4">
              <Field label="User name">
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    value={userName}
                    onChange={(e) => setUserName(e.target.value)}
                    autoComplete="username"
                    autoFocus
                    required
                  />
                )}
              </Field>

              <Field label="Password" error={error ?? undefined}>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="current-password"
                    invalid={error !== null}
                    required
                  />
                )}
              </Field>

              <Button type="submit" loading={busy} className="mt-1 w-full">
                Sign in
              </Button>
            </form>
          </CardContent>
        </Card>
        )}
      </div>
    </div>
  )
}
