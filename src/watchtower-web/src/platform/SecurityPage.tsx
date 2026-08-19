// The one screen an account manages its own credentials from. Reachable by any signed-in account in any
// realm, which is why it is platform and why it talks to `/api/auth/mfa/*` rather than a JSON-RPC handler
// (see `lib/mfa.ts`).
import { useEffect, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import QRCode from 'qrcode'
import { KeyRound, ShieldCheck, ShieldOff } from 'lucide-react'
import {
  beginTotp,
  confirmTotp,
  disableTotp,
  getMfaStatus,
  regenerateRecoveryCodes,
  type MfaEnrolment,
} from '@/lib/mfa'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { CopyButton } from '@/components/ui/copy-button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/use-toast'

/** Below this many unused codes the page starts saying so — running out means an administrator reset. */
const LOW_RECOVERY_CODES = 3

const STATUS_KEY = ['mfa-status']

export function SecurityPage() {
  const qc = useQueryClient()
  const { data: status, isLoading, isError, refetch } = useQuery({
    queryKey: STATUS_KEY,
    queryFn: getMfaStatus,
  })

  const [setupOpen, setSetupOpen] = useState(false)
  const [disableOpen, setDisableOpen] = useState(false)
  const [regenerateOpen, setRegenerateOpen] = useState(false)

  function invalidate() {
    qc.invalidateQueries({ queryKey: STATUS_KEY })
  }

  return (
    <div className="flex flex-col gap-6">
      <SectionHeader
        eyebrow="Your account"
        title="Security"
        description="Protect your sign-in with a second factor from an authenticator app."
      />

      {isError ? (
        <Banner
          tone="danger"
          title="Could not load your security settings."
          action={
            <Button variant="secondary" size="sm" onClick={() => void refetch()}>
              Retry
            </Button>
          }
        />
      ) : isLoading || !status ? (
        <Skeleton className="h-32 w-full" />
      ) : (
        <Card>
          <CardContent className="flex flex-col gap-4 pt-4 md:pt-5">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="flex min-w-0 items-start gap-3">
                <span
                  className={
                    status.totpEnabled
                      ? 'mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-md bg-ok-bg text-ok'
                      : 'mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-md bg-surface-2 text-text-3'
                  }
                >
                  {status.totpEnabled ? (
                    <ShieldCheck className="size-[18px]" />
                  ) : (
                    <ShieldOff className="size-[18px]" />
                  )}
                </span>
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-text">
                    Two-factor authentication is {status.totpEnabled ? 'on' : 'off'}
                  </p>
                  <p className="mt-0.5 text-[13px] text-text-2">
                    {status.totpEnabled
                      ? 'Signing in asks for a code from your authenticator app after your password.'
                      : 'Your password is currently the only thing protecting this account.'}
                  </p>
                </div>
              </div>

              {status.totpEnabled ? (
                <Button variant="secondary" size="sm" onClick={() => setDisableOpen(true)}>
                  Turn off
                </Button>
              ) : (
                <Button size="sm" onClick={() => setSetupOpen(true)}>
                  Set up
                </Button>
              )}
            </div>

            {status.totpEnabled && (
              <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
                <div className="min-w-0">
                  <p className="text-sm font-medium text-text">Recovery codes</p>
                  <p className="mt-0.5 text-[13px] text-text-2">
                    {status.recoveryCodesRemaining === 1
                      ? '1 unused code left.'
                      : `${status.recoveryCodesRemaining} unused codes left.`}{' '}
                    Each one signs you in once if your authenticator is unavailable.
                  </p>
                </div>
                <Button variant="secondary" size="sm" onClick={() => setRegenerateOpen(true)}>
                  <KeyRound />
                  Generate new codes
                </Button>
              </div>
            )}

            {status.totpEnabled && status.recoveryCodesRemaining < LOW_RECOVERY_CODES && (
              <Banner tone="warn" title="You are running low on recovery codes.">
                Generate a new set while you still have your authenticator — once both are gone, only an
                administrator can restore access to this account.
              </Banner>
            )}
          </CardContent>
        </Card>
      )}

      <SetupDialog
        open={setupOpen}
        onOpenChange={setSetupOpen}
        onEnrolled={() => {
          invalidate()
          toast.success('Two-factor authentication is on.')
        }}
      />

      <DisableDialog
        open={disableOpen}
        onOpenChange={setDisableOpen}
        onDisabled={() => {
          invalidate()
          toast.success('Two-factor authentication is off.')
        }}
      />

      <RegenerateDialog
        open={regenerateOpen}
        onOpenChange={setRegenerateOpen}
        onGenerated={invalidate}
      />
    </div>
  )
}

// ── Setup ────────────────────────────────────────────────────────────────────

/** Scan the key, prove it works, then save the codes — in that order, and none of them skippable. */
type SetupStep = 'scan' | 'confirm' | 'codes'

function SetupDialog({
  open,
  onOpenChange,
  onEnrolled,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  onEnrolled: () => void
}) {
  const [step, setStep] = useState<SetupStep>('scan')
  const [enrolment, setEnrolment] = useState<MfaEnrolment | null>(null)
  const [code, setCode] = useState('')
  const [codes, setCodes] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)

  // The key is minted when the dialog opens and discarded when it closes: it is a secret, and one that
  // exists only for as long as this dialog does. Closing before confirming simply abandons it — two-factor
  // stays off, so nothing is left half-enabled.
  const begin = useMutation({
    mutationFn: beginTotp,
    onSuccess: setEnrolment,
    onError: (failure: Error) => setError(failure.message || 'Could not start setup.'),
  })

  const confirm = useMutation({
    mutationFn: (value: string) => confirmTotp(value),
    onSuccess: (recoveryCodes) => {
      setCodes(recoveryCodes)
      setStep('codes')
      onEnrolled()
    },
    onError: (failure: Error) => setError(failure.message || 'That code is not valid.'),
  })

  useEffect(() => {
    if (!open) return
    setStep('scan')
    setEnrolment(null)
    setCode('')
    setCodes([])
    setError(null)
    begin.mutate()
    // Intentionally keyed on `open` alone: this is the dialog's own lifecycle, not a data dependency.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    confirm.mutate(code)
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* No close affordance on the codes step: they cannot be shown again, so dismissing by accident is
          the one mistake this dialog must not make easy. */}
      <DialogContent hideClose={step === 'codes'}>
        {step === 'scan' && (
          <>
            <DialogHeader>
              <DialogTitle>Set up two-factor authentication</DialogTitle>
              <DialogDescription>
                Scan this code with an authenticator app, then continue to confirm it works.
              </DialogDescription>
            </DialogHeader>

            {error ? (
              <Banner tone="danger" title={error} />
            ) : !enrolment ? (
              <Skeleton className="h-[200px] w-full" />
            ) : (
              <div className="flex flex-col items-center gap-4">
                <QrCode value={enrolment.otpauthUri} />
                <div className="w-full">
                  <p className="mb-1.5 text-xs text-text-3">
                    No camera? Enter this key in your app instead.
                  </p>
                  <div className="flex items-center gap-2">
                    <code className="min-w-0 flex-1 truncate rounded-md border border-border bg-surface-2 px-2.5 py-1.5 font-mono text-[13px] text-text">
                      {enrolment.sharedKey}
                    </code>
                    <CopyButton value={enrolment.sharedKey} />
                  </div>
                </div>
              </div>
            )}

            <DialogFooter>
              <Button variant="secondary" onClick={() => onOpenChange(false)}>
                Cancel
              </Button>
              <Button disabled={!enrolment} onClick={() => { setError(null); setStep('confirm') }}>
                Continue
              </Button>
            </DialogFooter>
          </>
        )}

        {step === 'confirm' && (
          <form onSubmit={onSubmit} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle>Confirm your authenticator</DialogTitle>
              <DialogDescription>
                Enter the 6-digit code your app is showing now. Two-factor authentication stays off until
                this matches.
              </DialogDescription>
            </DialogHeader>

            <Field label="Authentication code" error={error ?? undefined}>
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  autoComplete="one-time-code"
                  inputMode="numeric"
                  maxLength={7}
                  autoFocus
                  invalid={error !== null}
                  required
                />
              )}
            </Field>

            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => setStep('scan')}>
                Back
              </Button>
              <Button type="submit" loading={confirm.isPending}>
                Turn on
              </Button>
            </DialogFooter>
          </form>
        )}

        {step === 'codes' && (
          <RecoveryCodesStep
            codes={codes}
            title="Save your recovery codes"
            onDone={() => onOpenChange(false)}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

// ── Disable / regenerate ─────────────────────────────────────────────────────

function DisableDialog({
  open,
  onOpenChange,
  onDisabled,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  onDisabled: () => void
}) {
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)

  const disable = useMutation({
    mutationFn: (value: string) => disableTotp(value),
    onSuccess: () => {
      onDisabled()
      onOpenChange(false)
    },
    onError: (failure: Error) => setError(failure.message || 'That code is not valid.'),
  })

  useEffect(() => {
    if (!open) return
    setCode('')
    setError(null)
  }, [open])

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <form
          onSubmit={(event) => {
            event.preventDefault()
            setError(null)
            disable.mutate(code)
          }}
          className="flex flex-col gap-4"
        >
          <DialogHeader>
            <DialogTitle>Turn off two-factor authentication</DialogTitle>
            <DialogDescription>
              Your password becomes the only thing protecting this account. Confirm with a code from your
              authenticator app — or one of your recovery codes, if the app is no longer available.
            </DialogDescription>
          </DialogHeader>

          <Field label="Authentication or recovery code" error={error ?? undefined}>
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                value={code}
                onChange={(e) => setCode(e.target.value)}
                autoComplete="one-time-code"
                maxLength={20}
                autoFocus
                invalid={error !== null}
                required
              />
            )}
          </Field>

          <DialogFooter>
            <Button type="button" variant="secondary" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="danger" loading={disable.isPending}>
              Turn off
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function RegenerateDialog({
  open,
  onOpenChange,
  onGenerated,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  onGenerated: () => void
}) {
  const [code, setCode] = useState('')
  const [codes, setCodes] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)

  const regenerate = useMutation({
    mutationFn: (value: string) => regenerateRecoveryCodes(value),
    onSuccess: (fresh) => {
      setCodes(fresh)
      onGenerated()
    },
    onError: (failure: Error) => setError(failure.message || 'That code is not valid.'),
  })

  useEffect(() => {
    if (!open) return
    setCode('')
    setCodes([])
    setError(null)
  }, [open])

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent hideClose={codes.length > 0}>
        {codes.length > 0 ? (
          <RecoveryCodesStep
            codes={codes}
            title="Your new recovery codes"
            onDone={() => onOpenChange(false)}
          />
        ) : (
          <form
            onSubmit={(event) => {
              event.preventDefault()
              setError(null)
              regenerate.mutate(code)
            }}
            className="flex flex-col gap-4"
          >
            <DialogHeader>
              <DialogTitle>Generate new recovery codes</DialogTitle>
              <DialogDescription>
                Your current codes stop working immediately. A code from your authenticator app is required
                — a recovery code is not accepted here, so one leaked code cannot be turned into ten.
              </DialogDescription>
            </DialogHeader>

            <Field label="Authentication code" error={error ?? undefined}>
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  autoComplete="one-time-code"
                  inputMode="numeric"
                  maxLength={7}
                  autoFocus
                  invalid={error !== null}
                  required
                />
              )}
            </Field>

            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => onOpenChange(false)}>
                Cancel
              </Button>
              <Button type="submit" loading={regenerate.isPending}>
                Generate
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  )
}

// ── Shared pieces ────────────────────────────────────────────────────────────

/**
 * The one screen the codes are ever readable on. The server keeps only hashes, so leaving here without
 * saving them means they are gone — which is why "Done" is deliberately gated behind an explicit
 * acknowledgement rather than being the easiest thing on the screen.
 */
function RecoveryCodesStep({
  codes,
  title,
  onDone,
}: {
  codes: string[]
  title: string
  onDone: () => void
}) {
  const [saved, setSaved] = useState(false)
  const asText = codes.join('\n')

  return (
    <>
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        <DialogDescription>
          Store these somewhere safe and offline. Each code signs you in once if your authenticator app is
          unavailable. They are shown only now and cannot be retrieved later.
        </DialogDescription>
      </DialogHeader>

      <ul className="grid grid-cols-2 gap-x-4 gap-y-1.5 rounded-md border border-border bg-surface-2 p-3 font-mono text-[13px] text-text">
        {codes.map((value) => (
          <li key={value}>{value}</li>
        ))}
      </ul>

      <div className="flex items-center justify-between gap-3">
        <CopyButton value={asText} label="Copy all" variant="secondary" />
        <label className="flex cursor-pointer items-center gap-2 text-[13px] text-text-2">
          <input
            type="checkbox"
            checked={saved}
            onChange={(e) => setSaved(e.target.checked)}
            className="size-4 accent-[var(--brand)]"
          />
          I have saved these codes
        </label>
      </div>

      <DialogFooter>
        <Button disabled={!saved} onClick={onDone}>
          Done
        </Button>
      </DialogFooter>
    </>
  )
}

/**
 * The enrolment URI as a scannable image.
 *
 * Rendered with fixed black-on-white rather than theme tokens on purpose: a QR code is read by a camera,
 * not by a person, and inverting it in dark mode is a well-known way to make scanners fail. The white
 * plate around it is the quiet zone the format requires.
 */
function QrCode({ value }: { value: string }) {
  const [dataUrl, setDataUrl] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false
    QRCode.toDataURL(value, { width: 200, margin: 1, color: { dark: '#000000', light: '#ffffff' } })
      .then((url) => {
        if (!cancelled) setDataUrl(url)
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })
    return () => {
      cancelled = true
    }
  }, [value])

  if (failed) {
    // Not an error worth blocking on: the manual key below it does the same job.
    return (
      <p className="text-[13px] text-text-2">
        The QR code could not be drawn — enter the key below in your app instead.
      </p>
    )
  }

  return dataUrl ? (
    <img
      src={dataUrl}
      alt="QR code for setting up your authenticator app"
      width={200}
      height={200}
      className="rounded-md bg-white p-2"
    />
  ) : (
    <Skeleton className="size-[200px]" />
  )
}
