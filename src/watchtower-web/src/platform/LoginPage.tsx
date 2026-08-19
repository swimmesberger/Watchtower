import { useEffect, useState, type FormEvent } from 'react'
import { Eye } from 'lucide-react'
import {
  AccessDeniedError,
  completeMfaLogin,
  continueSession,
  isChallengeExpired,
  login,
  LoginError,
  safeRedirectTarget,
  type LoginOutcome,
  type MfaChallenge,
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
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // Present only between the password being accepted and the second factor being supplied. Holding it in
  // component state (never in storage) is deliberate: it is a one-shot credential with a five-minute life,
  // and a reload legitimately means starting over.
  const [challenge, setChallenge] = useState<MfaChallenge | null>(null)
  const [code, setCode] = useState('')
  const [useRecoveryCode, setUseRecoveryCode] = useState(false)
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

  /**
   * A full document load rather than a router navigation: the capability snapshot and the contribution
   * registry are both resolved once per boot in `main.tsx`, so this is what re-runs that bootstrap against
   * the new identity instead of carrying the anonymous one across the sign-in boundary. For the
   * cross-domain case it is not a choice at all — the target is another origin.
   */
  function land(outcome: Extract<LoginOutcome, { kind: 'signed-in' }>) {
    window.location.assign(outcome.result.continueUrl ?? safeRedirectTarget(redirect))
  }

  /** Back to the credentials form, with the reason stated rather than an unexplained empty box. */
  function restart(message: string) {
    setChallenge(null)
    setCode('')
    setUseRecoveryCode(false)
    setPassword('')
    setError(null)
    setNotice(message)
  }

  function onFailure(failure: unknown, fallback: string) {
    if (failure instanceof AccessDeniedError) {
      setHandover('denied')
      setError(failure.message)
    } else if (failure instanceof LoginError) {
      setError(failure.message)
    } else {
      const message = failure instanceof Error ? failure.message : fallback
      setError(message)
      toast.error('Could not reach Watchtower.', message)
    }
  }

  async function onSubmitCredentials(event: FormEvent) {
    event.preventDefault()
    if (busy) return

    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const outcome = await login(userName, password, redirectUri)
      if (outcome.kind === 'mfa-required') {
        // The password alone got nothing: no cookie was set, and this token is the only thing that can
        // finish the sign-in.
        setChallenge(outcome.challenge)
        setBusy(false)
        return
      }
      land(outcome)
    } catch (failure) {
      setBusy(false)
      onFailure(failure, 'Sign-in failed.')
    }
  }

  async function onSubmitCode(event: FormEvent) {
    event.preventDefault()
    if (busy || !challenge) return

    setBusy(true)
    setError(null)
    try {
      const outcome = await completeMfaLogin(
        challenge,
        useRecoveryCode ? { recoveryCode: code } : { code },
        redirectUri,
      )
      if (outcome.kind === 'signed-in') {
        land(outcome)
        return
      }
      // Unreachable today — the second-factor endpoint answers a success or a 401, never another
      // challenge — but the union it shares with the password step allows the shape, and a missing branch
      // here would leave the button spinning for ever with no way forward. Start over instead.
      restart('Something went wrong finishing your sign-in. Please try again.')
      setBusy(false)
    } catch (failure) {
      setBusy(false)
      // The backend answers a wrong code and a lapsed challenge identically — saying which would tell a
      // caller holding a stolen password whether the window is still worth grinding. This client knows how
      // long ago it asked, so it can offer the right recovery without the server ever having said.
      if (failure instanceof LoginError && isChallengeExpired(challenge)) {
        restart('Your sign-in request timed out. Please enter your password again.')
        return
      }
      onFailure(failure, 'Sign-in failed.')
    }
  }

  const heading = handover === 'denied'
    ? 'Access denied'
    : challenge
      ? 'Two-factor authentication'
      : 'Sign in to Watchtower'

  return (
    <div className="flex min-h-dvh items-center justify-center px-4 py-10">
      <div className="w-full max-w-[360px]">
        <div className="mb-6 flex flex-col items-center gap-3">
          <span className="flex size-10 items-center justify-center rounded-lg bg-brand-soft">
            <Eye className="size-5 text-brand" />
          </span>
          <h1 className="text-lg font-bold tracking-tight text-text">{heading}</h1>
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
        ) : challenge ? (
          <Card>
            <CardContent>
              <form onSubmit={onSubmitCode} className="flex flex-col gap-4">
                <p className="text-sm text-text-2">
                  {useRecoveryCode
                    ? 'Enter one of the recovery codes you saved when you set up two-factor authentication.'
                    : 'Enter the 6-digit code from your authenticator app.'}
                </p>

                <Field label={useRecoveryCode ? 'Recovery code' : 'Authentication code'} error={error ?? undefined}>
                  {({ id, describedBy }) => (
                    <Input
                      id={id}
                      aria-describedby={describedBy}
                      value={code}
                      onChange={(e) => setCode(e.target.value)}
                      // one-time-code drives the OS autofill that reads the code out of a notification;
                      // a recovery code comes off paper, so there is nothing to suggest.
                      autoComplete={useRecoveryCode ? 'off' : 'one-time-code'}
                      inputMode={useRecoveryCode ? 'text' : 'numeric'}
                      maxLength={useRecoveryCode ? 20 : 7}
                      autoFocus
                      invalid={error !== null}
                      required
                    />
                  )}
                </Field>

                <Button type="submit" loading={busy} className="mt-1 w-full">
                  Verify
                </Button>

                <button
                  type="button"
                  onClick={() => {
                    setUseRecoveryCode((previous) => !previous)
                    setCode('')
                    setError(null)
                  }}
                  className="text-xs text-text-2 underline-offset-2 hover:text-text hover:underline"
                >
                  {useRecoveryCode
                    ? 'Use your authenticator app instead'
                    : 'Use a recovery code instead'}
                </button>
              </form>
            </CardContent>
          </Card>
        ) : (
          <Card>
            <CardContent>
              <form onSubmit={onSubmitCredentials} className="flex flex-col gap-4">
                {notice && <p className="text-sm text-text-2">{notice}</p>}

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
