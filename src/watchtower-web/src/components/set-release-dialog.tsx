import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRouteContext } from '@tanstack/react-router'
import { api } from '@/lib/api'
import type { Release, VersionState } from '@/lib/types'
import { rosterVersion } from '@/lib/release'
import { timeAgo } from '@/lib/format'
import { useProductReleases } from '@/hooks/use-product-releases'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
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
import { toast } from '@/components/ui/use-toast'

// ── The roll-out dialog (ADR-0026 stage 6) ───────────────────────────────────────
//
// design.md §"Product detail page": segmented *Pin to v1.4.0* / *Set to track latest*, an instance
// checklist with current versions, and a live consequence sentence. One component for both places it
// is opened from — the Instances roster's bulk action and the Releases tab's per-row action — because
// they are the same operation with a different starting selection.
//
// It lives in `components/` rather than in a module for the reason `lib/release.ts` does: the two
// callers are in different modules (templates and products), and modules never import each other.

/** One row of the checklist: a stack that could take the new version, and where it is now. */
export interface ReleaseTarget {
  /** The stack to write when this row is applied on its own. */
  stackId: number
  /** What to call it: a tenant slug on the Instances roster, a stack name elsewhere. */
  label: string
  /** Its pin and its deployed release — what the row's current-version cell reads. */
  state: VersionState
  /** A stopped stack is pinned successfully and simply not deployed; the row says so. */
  stopped?: boolean
}

/**
 * The template this dialog can write a fleet default for. Present on the Instances roster, absent for
 * a product with no tenancy — see the note on the two apply paths below.
 */
export interface ReleaseFleet {
  templateId: number
  templateName: string
}

/**
 * Pin a set of instances to one release, or set them back to tracking latest.
 *
 * **Two apply paths, and which one runs is decided by the checklist**, deliberately:
 *
 * - **Every row selected, and a `fleet` given** → one `templates.setTenantsRelease` call, which writes
 *   the pin onto every tenant *and* stores it as the template's default, so the tenant provisioned
 *   tomorrow starts where the fleet is. That is the fleet operation, and it is one round trip however
 *   many tenants there are.
 * - **A subset selected, or no `fleet`** → one `stacks.setRelease` per selected row, and the template
 *   default is left alone. That is the canary and per-tenant-hotfix case design.md §"Rollback and
 *   canary" is built on: a few instances move, the fleet default does not, and the next tenant still
 *   joins where the fleet is.
 *
 * The consequence sentence states which of the two is about to happen, because the difference is
 * invisible otherwise and it is the difference between "the fleet is on 1.4.0" and "three of them are".
 */
export function SetReleaseDialog({
  open,
  onOpenChange,
  productId,
  targets,
  fleet,
  seedReleaseId,
  onApplied,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  productId: number
  targets: ReleaseTarget[]
  fleet?: ReleaseFleet | null
  /** Pre-select this release — the Releases tab's row action. Undefined starts from what they run. */
  seedReleaseId?: number | null
  /** Called after a successful apply, so the caller can invalidate what it owns. */
  onApplied?: () => void
}) {
  const qc = useQueryClient()
  // The pre-rollout backup checkbox is only honest where the Backups module exists to honour it.
  const { caps } = useRouteContext({ from: '__root__' })
  const backupsEnabled = caps.isModuleEnabled('Backups')
  const { data, showOlder, hasOlder, loadingOlder } = useProductReleases(productId, open)
  const releases = useMemo(() => data?.releases ?? [], [data])
  const newestId = releases[0]?.id ?? null

  const [pin, setPin] = useState<number | null>(seedReleaseId ?? null)
  const [selected, setSelected] = useState<ReadonlySet<number>>(() => new Set())
  const [deploy, setDeploy] = useState(true)
  /**
   * "Back up each instance before deploying" — the operational answer to the caveat that Watchtower
   * rolls code and images back but never the application's database (design.md §"Rollback and
   * canary"). Off by default: it turns a rollout into a serial run that takes as long as N backups,
   * which is a cost to opt into rather than one to discover.
   */
  const [backupFirst, setBackupFirst] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const allIds = useMemo(() => targets.map((t) => t.stackId), [targets])
  const allIdsKey = allIds.join(',')
  /** The roster this opening was last reconciled against; null between openings. */
  const seededIdsRef = useRef<string | null>(null)

  // Re-seeded on every opening, not once at mount — the same rule the stack Version dialog follows,
  // and for the same reason: the instance outlives its openings, so a cancelled selection must not
  // survive into the next one. Everything selected by default, because the fleet operation is the
  // common one and it is also the only one that moves the template default.
  //
  // A roster that changes *while the dialog is open* (a tenant provisioned in another tab, a poll
  // landing) must not re-seed: blindly selecting everything again would silently turn a deliberate
  // three-of-twenty subset into a fleet write — which is a different call, against different rows,
  // that also moves the template default. So the selection is intersected with the new ids, and only
  // a selection that *was* everything grows to cover the new roster.
  useEffect(() => {
    if (!open) {
      seededIdsRef.current = null
      return
    }
    const ids = allIdsKey === '' ? [] : allIdsKey.split(',').map(Number)
    const seenIdsKey = seededIdsRef.current
    seededIdsRef.current = allIdsKey
    if (seenIdsKey === null) {
      setPin(seedReleaseId ?? null)
      setSelected(new Set(ids))
      setDeploy(true)
      setBackupFirst(false)
      setError(null)
      return
    }
    if (seenIdsKey === allIdsKey) return
    const seenIds = seenIdsKey === '' ? [] : seenIdsKey.split(',').map(Number)
    setSelected((current) => {
      const wasAll = current.size === seenIds.length && seenIds.every((id) => current.has(id))
      return wasAll ? new Set(ids) : new Set(ids.filter((id) => current.has(id)))
    })
  }, [open, seedReleaseId, allIdsKey])

  const selectedTargets = targets.filter((t) => selected.has(t.stackId))
  // A template with no instances yet is still a fleet write: it has a default to set, which is the
  // whole point of setting one before the first tenant exists. `templates.setTenantsRelease` supports
  // and tests exactly that, so the dialog must not disable Apply over an empty roster.
  const everySelected = targets.length === 0 || selectedTargets.length === targets.length
  const asFleet = fleet != null && everySelected

  // **The label, and the one thing it must never do: imply "track latest" while Apply pins.**
  // `releases` is the newest-20 window, so `find` misses on two entirely normal paths — the list has
  // not loaded yet, and the pin is older than the window (the same limit "N behind" degrades on).
  // Everything below therefore branches on `pin`, never on whether a version string was found; the
  // label falls back to the id rather than to silence.
  const pinnedVersion = releases.find((r) => r.id === pin)?.version
  const pinLabel =
    pin == null
      ? null
      : (pinnedVersion
        ?? (data != null ? `release #${pin} (outside the loaded list)` : `release #${pin}`))

  const apply = useMutation({
    mutationFn: async () => {
      const backingUp = deploy && backupFirst
      if (asFleet) {
        const result = await api.templates.setTenantsRelease(
          fleet!.templateId, pin, deploy, backingUp)
        // The server's own counts, not the checklist's: a tenant provisioned since this dialog
        // opened is written by the fleet call and absent from the list it was rendered from.
        return {
          written: result.tenantCount,
          deployed: result.deployed,
          backedUp: result.backedUp ?? 0,
        }
      }
      // Sequential rather than parallel: each of these can enqueue a deploy, and firing a fleet's
      // worth at once only races them into the same instance-wide gate. The first refusal stops the
      // run and is surfaced verbatim — a half-applied set is visible in the roster either way, and
      // pushing on past a refusal would hide the reason behind the last one.
      let deployed = 0
      let backedUp = 0
      for (const target of selectedTargets) {
        const result = await api.stacks.setRelease(target.stackId, pin, deploy, backingUp)
        if (result.deployed) deployed += 1
        // `!= null`: the API omits nulls, so a plain deploy leaves the field undefined.
        if (result.backupEventId != null) backedUp += 1
      }
      return { written: selectedTargets.length, deployed, backedUp }
    },
    onSuccess: ({ written, deployed, backedUp }) => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['product', productId] })
      if (fleet) {
        qc.invalidateQueries({ queryKey: ['tenants', fleet.templateId] })
        qc.invalidateQueries({ queryKey: ['template', fleet.templateId] })
      }
      const noun = fleet ? 'instance' : 'deployment'
      toast.success(
        pinLabel ? `Pinned to ${pinLabel}.` : 'Now tracking latest.',
        `${written} ${noun}${written === 1 ? '' : 's'} updated`
          + (!deploy
            ? '. Nothing was deployed.'
            // Backing up first means nothing is deploying *yet*: each deploy is chained to its own
            // backup and only runs if that succeeds, so reporting "N deploying" would be a lie.
            : backedUp > 0
              ? `, ${backedUp} backing up first — each deploys when its backup succeeds.`
              : `, ${deployed} deploying.`),
      )
      onApplied?.()
      onOpenChange(false)
    },
    // Verbatim: the server names the missing digest, the registry that did not answer, or the mode
    // that refuses a pin — every one of those beats a sentence this dialog would invent.
    onError: (err: Error) => setError(err.message),
  })

  const toggle = (stackId: number) =>
    setSelected((previous) => {
      const next = new Set(previous)
      if (!next.delete(stackId)) next.add(stackId)
      return next
    })

  const nothingToDo = selectedTargets.length === 0 && !asFleet
  // How many will actually be deployed (and therefore backed up first): a stopped instance is pinned
  // and skipped, so counting the whole selection would overstate how long a serial backup run takes.
  const deployingCount = selectedTargets.filter((t) => !t.stopped).length

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{fleet ? 'Set instances’ release' : 'Set release'}</DialogTitle>
          <DialogDescription>
            {fleet
              ? `Version policy for ${fleet.templateName}’s instances.`
              : 'Version policy for this product’s deployments.'}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          {/* The two radio labels are the explanation, as on the stack Version dialog — one place
              teaches latest-vs-pin, and this is the fleet-scale form of the same choice. */}
          <div className="space-y-2">
            <label className="flex cursor-pointer items-start gap-3 rounded-md border border-border p-3 hover:bg-surface-2">
              <input
                type="radio"
                name="set-release-mode"
                checked={pin == null}
                onChange={() => setPin(null)}
                className="mt-0.5 size-4 shrink-0 accent-[var(--brand)]"
              />
              <span className="min-w-0">
                <span className="block text-sm font-medium text-text">Track latest</span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Deploys the newest release as soon as it’s built.
                  {releases[0] && <> Currently {releases[0].version}.</>}
                </span>
              </span>
            </label>

            <div className="rounded-md border border-border p-3">
              <label className="flex cursor-pointer items-start gap-3">
                <input
                  type="radio"
                  name="set-release-mode"
                  checked={pin != null}
                  onChange={() => setPin(pin ?? seedReleaseId ?? releases[0]?.id ?? null)}
                  // Not merely "the window is empty": a seeded pin during the load phase, and a pin
                  // older than the window, are both states where pinning is exactly what is happening.
                  disabled={releases.length === 0 && pin == null}
                  className="mt-0.5 size-4 shrink-0 accent-[var(--brand)]"
                />
                <span className="text-sm font-medium text-text">Pin to a release</span>
              </label>
              <div className="mt-2 pl-7">
                <Select
                  value={pin != null ? String(pin) : ''}
                  onValueChange={(v) => setPin(Number(v))}
                  disabled={releases.length === 0 && pin == null}
                >
                  <SelectTrigger
                    aria-label="Release to pin to"
                    disabled={releases.length === 0 && pin == null}
                  >
                    <SelectValue placeholder="Select a release" />
                  </SelectTrigger>
                  <SelectContent>
                    {/* A pin older than the loaded window gets a row of its own, so the trigger names
                        it instead of falling back to the "Select a release" placeholder — which reads
                        as "nothing is pinned" over a dialog whose Apply pins. It cannot be re-selected
                        from the window, but it can be read, and the reader can move off it. */}
                    {pin != null && pinnedVersion === undefined && (
                      <SelectItem value={String(pin)}>{pinLabel}</SelectItem>
                    )}
                    {releases.map((release: Release) => (
                      <SelectItem key={release.id} value={String(release.id)}>
                        {release.version} · {timeAgo(release.createdAt)}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {/* Below the list rather than inside it: a Radix `SelectContent` is a listbox, and a
                    control in it that is not an option fights the keyboard and typeahead. Here it also
                    says how far the list currently reaches, which the select alone cannot. */}
                {hasOlder && (
                  <p className="mt-1.5 text-[12.5px] text-text-3">
                    Showing the newest {releases.length}.{' '}
                    <Button variant="link" loading={loadingOlder} onClick={() => showOlder()}>
                      Show older
                    </Button>
                  </p>
                )}
              </div>
            </div>
          </div>

          {targets.length > 0 && (
            <div>
              <div className="mb-1.5 flex items-center justify-between gap-2">
                <span className="text-[13px] font-medium text-text">
                  {fleet ? 'Instances' : 'Deployments'}
                </span>
                <Button
                  variant="link"
                  onClick={() =>
                    setSelected(everySelected ? new Set() : new Set(allIds))
                  }
                >
                  {everySelected ? 'Clear all' : 'Select all'}
                </Button>
              </div>
              <ul className="max-h-56 divide-y divide-border overflow-y-auto rounded-md border border-border">
                {targets.map((target) => {
                  const current = rosterVersion(target.state, newestId)
                  return (
                    <li key={target.stackId}>
                      <label className="flex cursor-pointer items-center gap-3 px-3 py-2 hover:bg-surface-2">
                        <input
                          type="checkbox"
                          checked={selected.has(target.stackId)}
                          onChange={() => toggle(target.stackId)}
                          className="size-4 shrink-0 accent-[var(--brand)]"
                        />
                        <span className="min-w-0 flex-1 truncate text-sm text-text">
                          {target.label}
                        </span>
                        {target.stopped && (
                          <Badge tone="neutral" size="sm">
                            stopped
                          </Badge>
                        )}
                        {current.pinned && (
                          <Badge tone="neutral" size="sm">
                            pinned
                          </Badge>
                        )}
                        <span className="shrink-0 font-mono text-[12.5px] text-text-2">
                          {current.version ?? '—'}
                        </span>
                      </label>
                    </li>
                  )
                })}
              </ul>
            </div>
          )}

          <label className="flex cursor-pointer items-center gap-3">
            <input
              type="checkbox"
              checked={deploy}
              onChange={(e) => setDeploy(e.target.checked)}
              className="size-4 shrink-0 accent-[var(--brand)]"
            />
            <span className="text-sm text-text">Deploy now</span>
          </label>

          {/* design.md §"Backups across tenants". Offered only when a deploy is actually going to
              happen — there is nothing to guard otherwise — and only when the backups module is
              enabled, because the checkbox would otherwise promise something nothing can do. */}
          {deploy && backupsEnabled && (
            <label className="flex cursor-pointer items-start gap-3">
              <input
                type="checkbox"
                checked={backupFirst}
                onChange={(e) => setBackupFirst(e.target.checked)}
                className="mt-0.5 size-4 shrink-0 accent-[var(--brand)]"
              />
              <span className="min-w-0">
                <span className="block text-sm text-text">
                  Back up each {fleet ? 'instance' : 'deployment'} before deploying
                </span>
                <span className="mt-0.5 block text-[13px] text-text-2">
                  Watchtower can roll code and images back, but never the application’s database — a
                  backup taken first is what makes a rollback safe. Each deploy runs only if its own
                  backup succeeded.
                  {deployingCount > 1 && (
                    <>
                      {' '}
                      <strong>Backups run one at a time</strong>, so {deployingCount} of them finish
                      well apart and the deploys trickle out behind them.
                    </>
                  )}
                </span>
              </span>
            </label>
          )}

          {/* The live consequence sentence. It says which of the two apply paths is about to run,
              because the difference — whether the template default moves with the instances — is
              invisible otherwise. */}
          <p className="text-[13px] text-text-2">
            {consequence({
              count: selectedTargets.length,
              stoppedCount: selectedTargets.filter((t) => t.stopped).length,
              pinLabel,
              deploy,
              backupFirst: deploy && backupsEnabled && backupFirst,
              fleetName: fleet?.templateName ?? null,
              movesFleetDefault: asFleet,
            })}
          </p>

          {error && (
            <Banner tone="danger" title="Couldn’t change the version">
              {error}
            </Banner>
          )}
        </div>

        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            loading={apply.isPending}
            disabled={apply.isPending || nothingToDo || (pin == null && releases.length === 0)}
            onClick={() => {
              setError(null)
              apply.mutate()
            }}
          >
            Apply
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/**
 * "3 instances will be pinned to 1.4.0 and deployed." — design.md's sentence, with the three things it
 * cannot leave out: that a stopped instance is pinned but not deployed, whether the fleet default moves
 * too, and — above all — whether Apply pins or unpins.
 *
 * <b><code>pinLabel</code> is null if and only if Apply will clear the pin.</b> It is never derived
 * from whether a version string could be found: the release window is 20 rows and loads
 * asynchronously, so a sentence that read "will go back to tracking latest" whenever the lookup missed
 * would describe the opposite of what the button does — on every seeded opening, for the whole load
 * phase, and permanently for a pin older than the window.
 */
function consequence({
  count,
  stoppedCount,
  pinLabel,
  deploy,
  backupFirst,
  fleetName,
  movesFleetDefault,
}: {
  count: number
  stoppedCount: number
  /** How to name the release Apply will pin to, or null to clear the pin. See the remarks. */
  pinLabel: string | null
  deploy: boolean
  /** Whether each deploy waits on its own backup — which changes "deployed" into "deployed if". */
  backupFirst: boolean
  fleetName: string | null
  /** Whether this apply also writes the template's default for future instances. */
  movesFleetDefault: boolean
}): string {
  const noun = fleetName ? 'instance' : 'deployment'
  const target = pinLabel ?? 'the latest release'

  if (count === 0) {
    return movesFleetDefault
      ? `No instances yet — new instances of ${fleetName} will start on ${target}.`
      : `Select at least one ${noun}.`
  }

  const deploying = deploy && count - stoppedCount > 0
  // "deployed after a backup" rather than "deployed": the deploy is conditional on the backup, and a
  // sentence that promised it outright would describe the happy path as the only path.
  const deployed = backupFirst ? 'deployed after a backup' : 'deployed'
  let sentence = `${count} ${noun}${count === 1 ? '' : 's'} `
    + (pinLabel
      // Two shapes rather than one clause bolted on, because "and deployed" only parses after the
      // passive half: "will go back to tracking latest and deployed" is not a sentence.
      ? `will be pinned to ${pinLabel}${deploying ? ` and ${deployed}` : ''}`
      : `will go back to tracking latest${deploying ? `, and be ${deployed}` : ''}`)
  sentence += '.'
  if (deploying && backupFirst)
    sentence += ' An instance whose backup fails is not deployed.'
  if (deploy && stoppedCount > 0) {
    sentence += ` ${stoppedCount} ${stoppedCount === 1 ? 'is' : 'are'} stopped, so `
      + `${stoppedCount === 1 ? 'it is' : 'they are'} pinned but not deployed.`
  }
  // The half the checklist decides: only a full selection moves what the *next* instance starts on.
  if (movesFleetDefault) sentence += ` New instances of ${fleetName} will start on ${target}.`
  else if (fleetName) sentence += ` ${fleetName}’s default for new instances is unchanged.`
  return sentence
}
