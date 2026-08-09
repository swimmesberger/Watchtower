import { useState, type FormEvent } from 'react'
import { Eye } from 'lucide-react'
import { login, LoginError, safeRedirectTarget } from '@/lib/auth'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/use-toast'
import { loginRoute } from './login-route'

export function LoginPage() {
  const { redirect } = loginRoute.useSearch()
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (busy) return

    setBusy(true)
    setError(null)
    try {
      await login(userName, password)
      // A full document load rather than a router navigation: the capability snapshot and the contribution
      // registry are both resolved once per boot in `main.tsx`, so this is what re-runs that bootstrap
      // against the new identity instead of carrying the anonymous one across the sign-in boundary.
      window.location.assign(safeRedirectTarget(redirect))
    } catch (failure) {
      setBusy(false)
      if (failure instanceof LoginError) {
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
          <h1 className="text-lg font-bold tracking-tight text-text">Sign in to Watchtower</h1>
        </div>

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
      </div>
    </div>
  )
}
