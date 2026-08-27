import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { ArrowLeft, Upload } from 'lucide-react'
import { api, uploadRestoreBundle } from '@/lib/api'
import type { RestoreFinding, RestoreValidation } from '@/lib/types'
import { absoluteTitle } from '@/lib/format'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/use-toast'
import { RecoveryChecklistCard } from './RecoveryChecklist'

/** How long to wait for Watchtower to answer again before saying so. */
const RESTART_TIMEOUT_MS = 5 * 60 * 1000

/** Gap between health probes while the instance is restarting. */
const RESTART_POLL_MS = 2000

/**
 * Restoring this Watchtower from a full backup bundle (ADR-0027).
 *
 * Behind the admin gate and behind a login, deliberately: an unauthenticated restore endpoint would be
 * an unauthenticated way to replace an instance, and "the instance looked empty" is not an
 * authorization decision. A fresh install signs in with the bootstrap admin first, then comes here.
 */
export function RestoreInstancePage() {
  const qc = useQueryClient()
  const fileInput = useRef<HTMLInputElement>(null)
  const [restarting, setRestarting] = useState(false)

  const { data: status, isLoading } = useQuery({
    queryKey: ['backups', 'restoreStatus'],
    queryFn: api.backups.getRestoreStatus,
  })

  const upload = useMutation({
    mutationFn: (file: File) => uploadRestoreBundle(file),
    onSuccess: validation => {
      qc.setQueryData(['backups', 'restoreStatus'], {
        ...(status ?? { freshInstance: false, lastOutcome: 'none', lastError: null, recoveryPending: false }),
        staged: validation,
      })
      void qc.invalidateQueries({ queryKey: ['backups', 'restoreStatus'] })
      toast.success(
        validation.canRestore
          ? 'Bundle read — review what it holds before restoring.'
          : 'Bundle read, but it cannot be restored here yet.',
      )
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'The upload failed.'),
  })

  const start = useMutation({
    mutationFn: api.backups.startInstanceRestore,
    onSuccess: () => setRestarting(true),
    onError: err => toast.error(err instanceof Error ? err.message : 'The restore could not be started.'),
  })

  const staged = status?.staged ?? null

  if (restarting) return <RestartingCard sourceInstance={staged?.instanceName ?? 'the backup'} />

  return (
    <div className="mx-auto flex w-full max-w-[720px] flex-col gap-6 p-6">
      <div>
        <Link
          to="/settings"
          className="inline-flex items-center gap-1 text-[13px] text-text-2 hover:text-text"
        >
          <ArrowLeft className="size-3.5" aria-hidden />
          Settings
        </Link>
        <h1 className="mt-2 text-lg font-semibold text-text">Restore this Watchtower</h1>
        <p className="mt-1 text-[13px] text-text-2">
          Replaces everything this Watchtower knows — its stacks, accounts, routes, settings and keys —
          with what is in a full backup bundle. Afterwards a checklist walks you through redeploying
          each stack and restoring its data.
        </p>
      </div>

      {isLoading ? (
        <Card>
          <CardContent className="flex flex-col gap-4">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : (
        <>
          {status?.lastOutcome === 'failed' && (
            <Banner tone="danger" title="The last restore did not complete">
              {status.lastError ?? 'The database was not replaced.'} Nothing was changed — this
              Watchtower is running on the database it had. The bundle is still here, so you can try
              again.
            </Banner>
          )}

          {status?.recoveryPending && <RecoveryChecklistCard />}

          <Card>
            <CardContent className="flex flex-col gap-4">
              <div>
                <h2 className="text-[13px] font-medium text-text">1. Upload the bundle</h2>
                <p className="mt-0.5 text-[13px] text-text-2">
                  The <span className="font-mono">.tar</span> file built by “Build bundle” on the
                  instance you are restoring from.
                </p>
              </div>

              <input
                ref={fileInput}
                type="file"
                accept=".tar,application/x-tar"
                className="hidden"
                onChange={e => {
                  const file = e.target.files?.[0]
                  if (file) upload.mutate(file)
                  // Cleared so choosing the same file twice fires the change event again.
                  e.target.value = ''
                }}
              />
              <div className="flex flex-wrap items-center gap-3">
                <Button
                  variant="secondary"
                  size="sm"
                  loading={upload.isPending}
                  disabled={upload.isPending}
                  onClick={() => fileInput.current?.click()}
                >
                  <Upload className="size-3.5" aria-hidden />
                  {staged ? 'Choose a different bundle' : 'Choose bundle'}
                </Button>
                {upload.isPending && (
                  <span className="text-[13px] text-text-2">
                    Uploading and checking it — a large bundle takes a while.
                  </span>
                )}
              </div>
            </CardContent>
          </Card>

          {staged && (
            <StagedBundleCard
              validation={staged}
              starting={start.isPending}
              onRestore={() => start.mutate()}
            />
          )}
        </>
      )}
    </div>
  )
}

/** What the uploaded bundle holds, what stops it, and the confirmation gate. */
function StagedBundleCard({
  validation,
  starting,
  onRestore,
}: {
  validation: RestoreValidation
  starting: boolean
  onRestore: () => void
}) {
  const [confirming, setConfirming] = useState(false)

  return (
    <Card>
      <CardContent className="flex flex-col gap-4">
        <div>
          <h2 className="text-[13px] font-medium text-text">2. Check what it holds</h2>
          <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-[13px]">
            <dt className="text-text-2">From</dt>
            <dd className="font-mono text-text">{validation.instanceName}</dd>
            <dt className="text-text-2">Built by</dt>
            <dd className="font-mono text-text">Watchtower {validation.appVersion}</dd>
            <dt className="text-text-2">Taken</dt>
            <dd className="text-text" title={absoluteTitle(validation.createdAtUtc)}>
              {new Date(validation.createdAtUtc).toLocaleString()}
            </dd>
            <dt className="text-text-2">Stacks</dt>
            <dd className="text-text">
              {validation.stackCount} with data
              {validation.missingStackCount > 0 &&
                `, ${validation.missingStackCount} without an archive`}
            </dd>
          </dl>
          {validation.stackNames.length > 0 && (
            <p className="mt-2 text-[13px] text-text-3">{validation.stackNames.join(', ')}</p>
          )}
        </div>

        {validation.blocking.map(finding => (
          <FindingBanner key={finding.code} tone="danger" finding={finding} />
        ))}
        {validation.warnings.map(finding => (
          <FindingBanner key={finding.code} tone="warn" finding={finding} />
        ))}

        <div>
          <h2 className="text-[13px] font-medium text-text">3. Restore</h2>
          <p className="mt-0.5 text-[13px] text-text-2">
            Watchtower stops for a few seconds while its database is replaced, then comes back. You
            will be signed out — sign in again with an account from{' '}
            <span className="font-mono">{validation.instanceName}</span>.
          </p>
        </div>

        <div>
          <Button
            variant="danger"
            size="sm"
            disabled={!validation.canRestore || starting}
            loading={starting}
            onClick={() => setConfirming(true)}
            title={validation.canRestore ? undefined : 'Resolve the blocking issues above first.'}
          >
            Restore this Watchtower
          </Button>
        </div>

        <ConfirmDialog
          open={confirming}
          onOpenChange={setConfirming}
          tone="danger"
          title="Replace this Watchtower’s database?"
          description={
            <>
              Everything this Watchtower knows now is replaced by the backup of{' '}
              <span className="font-mono">{validation.instanceName}</span>. Containers it deployed keep
              running, unmanaged, until you redeploy them from the checklist afterwards. This cannot be
              undone.
            </>
          }
          confirmLabel="Restore"
          requireText={validation.instanceName}
          loading={starting}
          onConfirm={() => {
            setConfirming(false)
            onRestore()
          }}
        />
      </CardContent>
    </Card>
  )
}

function FindingBanner({ finding, tone }: { finding: RestoreFinding; tone: 'danger' | 'warn' }) {
  return (
    <Banner tone={tone} title={tone === 'danger' ? 'This bundle cannot be restored here' : 'Worth knowing'}>
      {finding.message}
    </Banner>
  )
}

/**
 * The wait while the coordinator stops Watchtower, replays and starts it again. The session dies with
 * the restart, so this polls the unauthenticated health endpoint rather than any API the old session
 * could still reach, and sends the operator to the login page once it answers.
 */
function RestartingCard({ sourceInstance }: { sourceInstance: string }) {
  const [tooLong, setTooLong] = useState(false)

  useEffect(() => {
    const startedAt = Date.now()
    // Watchtower is still answering right now — the coordinator waits a moment so this very request can
    // return before it stops the container. So a health check that succeeds proves nothing until one
    // has *failed* first: going away is what says the restore actually started.
    let wentDown = false
    let cancelled = false

    const probe = async () => {
      const ok = await fetch('/health', { cache: 'no-store' })
        .then(r => r.ok)
        .catch(() => false)
      if (cancelled) return
      if (!ok) {
        wentDown = true
      } else if (wentDown) {
        // Down and back up: the restore is over, whatever its outcome, and the session went with it.
        window.location.assign('/login')
        return
      }
      if (Date.now() - startedAt > RESTART_TIMEOUT_MS) setTooLong(true)
    }

    void probe()
    const timer = window.setInterval(() => void probe(), RESTART_POLL_MS)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [])

  return (
    <div className="mx-auto flex w-full max-w-[720px] flex-col gap-6 p-6">
      <Card>
        <CardContent className="flex flex-col gap-3">
          <div className="flex items-center gap-2">
            <span
              className="size-2 rounded-full bg-run motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]"
              aria-hidden
            />
            <h1 className="text-sm font-semibold text-text">Restoring…</h1>
          </div>
          <p className="text-[13px] text-text-2">
            Watchtower is being stopped, its database replaced with the backup of{' '}
            <span className="font-mono">{sourceInstance}</span>, and started again. This page reconnects
            on its own and sends you to the sign-in form.
          </p>
          <p className="text-[13px] text-text-3">
            Sign in with an account from <span className="font-mono">{sourceInstance}</span> — the
            accounts this Watchtower had are gone.
          </p>
          {tooLong && (
            <Banner tone="warn" title="This is taking longer than expected">
              Watchtower has not answered for a few minutes. Check the{' '}
              <span className="font-mono">watchtower-restore-*</span> container’s log on the host — it
              always restarts Watchtower, whatever the outcome of the replay.
            </Banner>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
