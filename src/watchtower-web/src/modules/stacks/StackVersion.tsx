import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, Play } from 'lucide-react'
import { api } from '@/lib/api'
import type { Stack } from '@/lib/types'
import {
  availableRelease,
  behindCount,
  deployTargetVersion,
  newestRelease,
  pinnedBehind,
  usesReleases,
} from '@/lib/release'
import { timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { SectionHeader } from '@/components/ui/section-header'
import { toast } from '@/components/ui/use-toast'

// ── The version UX (ADR-0026 stage 4b) ───────────────────────────────────────────
//
// The three surfaces of invariant 6 — the header fragment, the Version dialog and the Version panel
// — in one file, because they must agree about what Deploy would apply. They agree by all reading
// `deployTargetVersion` and the other derivations in lib/release.ts; nothing here re-derives one.

/** How many releases the pin picker offers, and the window "N behind" is exact within. */
const RELEASE_OPTIONS = 20

/**
 * The product's newest releases, newest first.
 *
 * Its own cache key rather than the Releases tab's infinite query: that one pages, this one is a
 * fixed window, and react-query dedupes the callers on the stack page (header fragment, dialog,
 * Version panel, containers empty state) into one request.
 */
export function useProductReleases(productId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['product', productId, 'releases', 'options'],
    queryFn: () => api.products.listReleases(productId, undefined, RELEASE_OPTIONS),
    enabled,
  })
}

// ── Header fragment ──────────────────────────────────────────────────────────────

/**
 * The version fragment of the stack header's meta line — **the header invariant** (design.md §Stack
 * detail): it always states the version the Deploy button will apply.
 *
 * Git mode renders exactly what it always did (`main@a1b2c3d`, plain text). Releases mode renders the
 * version as a button onto the Version dialog, because pinning is an operational act rather than a
 * settings edit.
 */
export function StackVersionFragment({ stack }: { stack: Stack }) {
  const [open, setOpen] = useState(false)
  const { data } = useProductReleases(stack.productId, usesReleases(stack))
  const releases = data?.releases

  if (!usesReleases(stack)) {
    return (
      <>
        {stack.branch}
        {stack.lastDeployedCommit && (
          <span title={`Deployed commit ${stack.lastDeployedCommit}`}>
            @{stack.lastDeployedCommit.slice(0, 8)}
          </span>
        )}
      </>
    )
  }

  const version = deployTargetVersion(stack, releases)
  const pinned = stack.pinnedRelease != null
  const behind = pinnedBehind(stack, releases) != null
  const count = behind ? behindCount(stack, releases) : null

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title="Change version"
        className="rounded font-mono text-text underline-offset-2 transition-colors hover:text-brand hover:underline focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
      >
        {version ?? 'no releases yet'}
        {!pinned && version && ' (latest)'}
      </button>
      {pinned && (
        <>
          {' '}
          <Badge tone="neutral" size="sm">
            pinned
          </Badge>
        </>
      )}
      {/* Quiet chips, never a banner: nagging someone for a deliberate choice is how a tool starts
          feeling hostile (design.md §Stack detail, Drift). */}
      {behind && (
        <>
          {' '}
          <Badge tone="neutral" size="sm">
            {count != null ? `${count} behind` : 'behind'}
          </Badge>
        </>
      )}
      <VersionDialog stack={stack} open={open} onOpenChange={setOpen} />
    </>
  )
}

// ── Version dialog ───────────────────────────────────────────────────────────────

/**
 * Track latest, or pin to a release. The two radio labels *are* the explanation — design.md is
 * explicit that nothing else is needed, and this dialog is the one place the latest/pin distinction is
 * taught.
 */
export function VersionDialog({
  stack,
  open,
  onOpenChange,
}: {
  stack: Stack
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const qc = useQueryClient()
  const { data } = useProductReleases(stack.productId, usesReleases(stack))
  const releases = data?.releases ?? []

  const currentPin = stack.pinnedRelease?.id ?? null
  const [pin, setPin] = useState<number | null>(currentPin)
  const [error, setError] = useState<string | null>(null)

  // Re-seeded on every opening, not once at mount. The dialog instance outlives its openings, so
  // seeding at mount meant a cancelled selection survived into the next one — and Save would then
  // apply a pin the reader had already backed out of. It also means a pin written elsewhere (the
  // product roll-out, another tab, this stack's own automation) is picked up rather than overwritten
  // with a stale value. Any refusal banner from the previous opening goes with it.
  useEffect(() => {
    if (!open) return
    setPin(currentPin)
    setError(null)
  }, [open, currentPin])

  const save = useMutation({
    mutationFn: (deploy: boolean) => api.stacks.setRelease(stack.id, pin, deploy),
    onSuccess: (result, deploy) => {
      qc.setQueryData(['stacks', stack.id], result.stack)
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stack.id, 'events'] })
      if (result.deployed) toast.info(`Deploying ${stack.name}…`)
      // Only when a deploy was actually asked for: a plain Save must not report that nothing was
      // deployed, because nothing was meant to be.
      else if (deploy)
        toast.success('Version saved.', 'This stack is stopped, so nothing was deployed.')
      else toast.success('Version saved.')
      onOpenChange(false)
    },
    // Verbatim: the server names the missing digest, the registry that did not answer, or the mode
    // that refuses a pin — every one of those is more useful than a sentence this dialog invents.
    onError: (err: Error) => setError(err.message),
  })

  // What "Track latest" would resolve to, independently of the pin this stack may currently carry.
  const latestVersion = newestRelease(stack, releases)?.version

  return (
    // One close path for all four ways out (Esc, scrim, the × and the footer Cancel), so the re-seed
    // above cannot be bypassed by the button that is easiest to reach.
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* No description: the radio labels carry the explanation, and an empty Radix description
          element would only add an announced blank. */}
      <DialogContent aria-describedby={undefined}>
        <DialogHeader>
          <DialogTitle>Version</DialogTitle>
        </DialogHeader>

        <div className="space-y-3">
          <label className="flex cursor-pointer items-start gap-3 rounded-md border border-border p-3 hover:bg-surface-2">
            <input
              type="radio"
              name={`stack-${stack.id}-version`}
              checked={pin == null}
              onChange={() => setPin(null)}
              className="mt-0.5 size-4 shrink-0 accent-[var(--brand)]"
            />
            <span className="min-w-0">
              <span className="block text-sm font-medium text-text">Track latest</span>
              <span className="mt-0.5 block text-[13px] text-text-2">
                Deploys the newest release as soon as it’s built.
                {latestVersion && <> Currently {latestVersion}.</>}
              </span>
            </span>
          </label>

          <div className="rounded-md border border-border p-3">
            <label className="flex cursor-pointer items-start gap-3">
              <input
                type="radio"
                name={`stack-${stack.id}-version`}
                checked={pin != null}
                onChange={() => setPin(pin ?? releases[0]?.id ?? null)}
                disabled={releases.length === 0}
                className="mt-0.5 size-4 shrink-0 accent-[var(--brand)]"
              />
              <span className="text-sm font-medium text-text">Pin to a release</span>
            </label>
            {/* Outside the label on purpose: a select inside one fights the label's own activation. */}
            <div className="mt-2 pl-7">
              <Select
                value={pin != null ? String(pin) : ''}
                onValueChange={(v) => setPin(Number(v))}
                disabled={releases.length === 0}
              >
                <SelectTrigger aria-label="Release to pin to" disabled={releases.length === 0}>
                  <SelectValue placeholder="Select a release" />
                </SelectTrigger>
                <SelectContent>
                  {releases.map((release) => (
                    <SelectItem key={release.id} value={String(release.id)}>
                      {release.version} · {timeAgo(release.createdAt)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
        </div>

        {error && (
          <Banner tone="danger" title="Couldn’t change the version">
            {error}
          </Banner>
        )}

        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            variant="secondary"
            loading={save.isPending && save.variables === false}
            disabled={save.isPending}
            onClick={() => {
              setError(null)
              save.mutate(false)
            }}
          >
            Save
          </Button>
          <Button
            loading={save.isPending && save.variables === true}
            disabled={save.isPending}
            onClick={() => {
              setError(null)
              save.mutate(true)
            }}
          >
            Save &amp; deploy
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// ── Version panel (Overview) ─────────────────────────────────────────────────────

/**
 * What the Updates panel is *replaced* by in `Releases` mode — never rendered beside it (invariant 4).
 *
 * No "Check now" and no per-image digest list: releases are pushed by CI, so there is nothing to poll,
 * and the header line says when the last one arrived instead.
 */
export function VersionPanel({ stack }: { stack: Stack }) {
  const qc = useQueryClient()
  const { data } = useProductReleases(stack.productId, true)
  const releases = data?.releases

  const isDeploying = stack.lastDeployStatus === 'running' || stack.lastDeployStatus === 'queued'

  const deploy = useMutation({
    mutationFn: () => api.stacks.deploy(stack.id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stack.id, 'events'] })
      toast.info(`Deploying ${stack.name}…`)
    },
    onError: (err: Error) => toast.error('Deploy failed', err.message),
  })

  // One button, and it names its target: in the drifted-and-behind case the drift line talks about
  // the deployed release while Deploy would apply a newer one, and a bare "Deploy" beside that
  // sentence reads as "redeploy what you have".
  const target = deployTargetVersion(stack, releases)
  const deployButton = (
    <Button
      variant="secondary"
      size="sm"
      loading={deploy.isPending || isDeploying}
      disabled={deploy.isPending || isDeploying || stack.desiredState === 'stopped'}
      onClick={() => deploy.mutate()}
    >
      {!(deploy.isPending || isDeploying) && <Play />}
      {target ? `Deploy ${target}` : 'Deploy'}
    </Button>
  )

  const pinned = stack.pinnedRelease
  const running = stack.lastDeployedRelease?.version
  const available = availableRelease(stack, releases)
  const behind = pinnedBehind(stack, releases)
  const drifted = stack.driftedContainers ?? []
  // Exactly one release means the product just flipped out of Git mode — see the sentence below.
  // Derived from the window this page already fetches rather than from a new backend field.
  const firstRelease = releases?.length === 1 && data?.hasMore === false

  return (
    <>
      <SectionHeader
        title="Version"
        // Where "Check now" lives in Git mode. Releases are pushed, so this is passive.
        action={
          releases?.[0] && (
            <span className="tnum text-[13px] text-text-3">
              Last release {timeAgo(releases[0].createdAt)}
            </span>
          )
        }
      />
      <Card>
        <CardContent className="space-y-3">
          <p className="text-sm text-text-2">
            {running ? (
              <>
                Running <span className="font-medium text-text">{running}</span>
              </>
            ) : (
              'No release deployed yet'
            )}{' '}
            · {pinned ? `Pinned to ${pinned.version}` : 'Tracking latest'}
          </p>

          {/* Exactly one of the three states. Pinned deliberately has no call to action: the header
              chips carry "behind", and a banner would nag someone for a deliberate choice. */}
          {pinned ? (
            behind && <p className="text-[13px] text-text-3">Latest is {behind.version}.</p>
          ) : available ? (
            <Banner tone="info" title={`${available.version} is available`} action={deployButton}>
              {available.createdAt ? `Released ${timeAgo(available.createdAt)}.` : 'Deploy to apply it.'}
            </Banner>
          ) : running ? (
            <div className="flex items-center gap-2">
              <CheckCircle2 className="size-4 shrink-0 text-ok" aria-hidden />
              <span className="text-sm text-text-2">
                Up to date — running the latest release, {running}
              </span>
            </div>
          ) : null}

          {/* Local drift: the containers are not on the digests the deployed release pins. Actionable
              and abnormal, so it gets a line with the fix rather than a chip. */}
          {drifted.length > 0 && running && (
            <div className="flex flex-wrap items-center gap-3 rounded-md border border-warn-bd bg-warn-bg px-3 py-2">
              <span className="min-w-0 flex-1 text-[13px] text-text">
                Containers do not match {running}: {drifted.join(', ')}
              </span>
              {deployButton}
            </div>
          )}

          {/* Self-clearing: the second release makes it disappear. The mode flip is announced rather
              than special-cased (design.md §"Update checks and drift"). */}
          {firstRelease && (
            <p className="text-[13px] text-text-2">
              This product now has releases. Updates are tracked as releases instead of raw image
              digests.
            </p>
          )}
        </CardContent>
      </Card>
    </>
  )
}
