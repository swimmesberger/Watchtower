// The release-mode derivations, as pure functions of what the DTOs say (ADR-0026 stage 4b).
//
// They live in lib/ rather than in the stacks module because three modules read them — stacks,
// dashboard and products — and modules do not import each other. Nothing here fetches, holds state
// or renders; `modules/stacks/StackVersion.tsx` is where those answers become UI.
//
// Two invariants are expressed here, and expressing them once is the point:
//
//   4. One update mechanism visible — `usesReleases` is the single predicate, mirroring the
//      backend's `ReleaseResolver.UsesReleases`.
//   6. Deploy shows what it will apply — `deployTargetVersion` is the single answer to "what would
//      this Deploy button do", so every surface that offers one reads the same value and there is
//      one place it can be wrong.
import type { Release, Stack } from './types'

/**
 * Whether this stack's product deploys releases rather than branch heads.
 *
 * **The mode is the switch, not the presence of releases**: a product can hold releases and stay in
 * Git mode until its next release flips it, exactly as the backend predicate does.
 */
export function usesReleases(stack: Stack): boolean {
  return stack.releaseMode === 'releases'
}

/** A release as the version surfaces need it: identity, label, and age when it is known. */
export interface ReleaseView {
  id: number
  version: string
  /** Null when the value came from the cached update check, which does not carry a timestamp. */
  createdAt: string | null
}

/**
 * The product's newest release, as well as the caller can know it — **one source, in one place**.
 *
 * The live list wins when the caller has one, because the cached update check is only rewritten by
 * the periodic check: between a CI publish and the next tick the DTO still describes the previous
 * world, and a page that reads both sources contradicts itself with no "Check now" to escape with
 * (there is none in Releases mode — releases are pushed).
 *
 * Without a list — a stack list row, a dashboard card — the DTO is all there is, and it answers:
 * `availableReleaseVersion` is the newest when it differs from the deployed release, and the
 * deployed release is the newest when it does not.
 */
export function newestRelease(stack: Stack, releases?: Release[]): ReleaseView | null {
  const live = releases?.[0]
  if (live) return { id: live.id, version: live.version, createdAt: live.createdAt }
  if (stack.availableReleaseId != null && stack.availableReleaseVersion != null)
    return { id: stack.availableReleaseId, version: stack.availableReleaseVersion, createdAt: null }
  if (stack.lastDeployedRelease)
    return { ...stack.lastDeployedRelease, createdAt: null }
  return null
}

/**
 * The version a Deploy right now would apply — invariant 6's single answer.
 *
 * A pin is exact. Tracking latest resolves `pin ?? newest` at execution time (invariant 3), so the
 * honest value is the newest release. Null only in Git mode's caller-side sense and in the one
 * reachable Releases-mode gap: a product reverted to Releases mode with no releases at all, where
 * every surface renders its Deploy control version-less, as it did before stage 4.
 */
export function deployTargetVersion(stack: Stack, releases?: Release[]): string | null {
  return stack.pinnedRelease?.version ?? newestRelease(stack, releases)?.version ?? null
}

/**
 * The release a latest-tracking stack has not deployed yet — what the Version panel's banner is for.
 * Null while the stack is already on the newest one, and always null for a pinned stack, which is
 * not tracking anything.
 */
export function availableRelease(stack: Stack, releases?: Release[]): ReleaseView | null {
  if (stack.pinnedRelease) return null
  const newest = newestRelease(stack, releases)
  // `>` rather than `!==`: ids are the ordering (invariant 7), so a stale cached candidate that is
  // older than what was deployed must read as "up to date", never as an available downgrade.
  return newest && newest.id > (stack.lastDeployedRelease?.id ?? -1) ? newest : null
}

/**
 * The newest release when a **pinned** stack is behind it — the quiet header chip, never a banner.
 *
 * Not merely "a newer release exists": saving a pin without deploying leaves the cached
 * `availableReleaseId` pointing at the pin itself until the next deploy, and calling that "behind"
 * would nag someone about the version they just chose.
 */
export function pinnedBehind(stack: Stack, releases?: Release[]): ReleaseView | null {
  const pin = stack.pinnedRelease
  if (!pin) return null
  const newest = newestRelease(stack, releases)
  // `>` rather than `!==` (invariant 7): a pin newer than every source of "newest" — pinned forward
  // before the update check catches up — is not behind anything.
  return newest && newest.id > pin.id ? newest : null
}

/**
 * How many releases are newer than the pin, or null when the fetched window cannot say — the pin is
 * older than the page the picker loaded, or the list has not arrived. The chip then reads "behind"
 * without a number rather than guessing one.
 */
export function behindCount(stack: Stack, releases?: Release[]): number | null {
  const pin = stack.pinnedRelease
  if (!releases || !pin) return null
  const index = releases.findIndex((r) => r.id === pin.id)
  return index < 0 ? null : index
}
