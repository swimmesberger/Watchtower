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
import type { Release, Stack, VersionState } from './types'

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

// ── Roster derivations (stage 6) ─────────────────────────────────────────────────
//
// The three above take a `Stack`, because they answer questions about a stack page. A roster row is a
// narrower thing — `VersionState`, which `Stack`, `ProductStack` and `Tenant` all satisfy — so the
// Instances table, the product roster and the roll-out dialog's checklist share one set of answers
// instead of three copies that would drift.

/** Which of the three buckets a roster row falls into. Disjoint, and they sum to the roster. */
export type VersionBucket = 'pinned' | 'onLatest' | 'behind'

/**
 * Where one row sits relative to the newest release.
 *
 * **Pinned wins over behind**, deliberately: a pinned-and-outdated instance is reported as pinned,
 * because a pin is a deliberate choice and counting it as "behind" would turn the rollup into a
 * standing complaint about it (design.md's rule that pinned stacks are never nagged, risk 10). The row
 * itself still carries a quiet `behind` chip; the rollup counts it once, as pinned.
 *
 * **`>=`, matching `rosterVersion`'s `>`.** The two must agree about what "behind" is or a row lands in
 * the behind bucket with no `behind` chip beside it to explain the count. A row *ahead* of the window's
 * idea of newest — deployed between the release list being fetched and the roster being rendered — is
 * on latest as far as anything here can tell, not behind it.
 *
 * A row that has never deployed counts as `behind`: it is not on latest, and it is not pinned.
 */
export function versionBucket(state: VersionState, newestId: number | null): VersionBucket {
  if (state.pinnedRelease) return 'pinned'
  const deployed = state.lastDeployedRelease?.id
  if (deployed == null) return 'behind'
  return newestId == null || deployed >= newestId ? 'onLatest' : 'behind'
}

/** The three counts the rollup line above a roster renders. */
export interface VersionRollup {
  onLatest: number
  pinned: number
  behind: number
  total: number
}

/** Buckets a whole roster in one pass. */
export function versionRollup(
  states: readonly VersionState[],
  newestId: number | null,
): VersionRollup {
  const rollup: VersionRollup = { onLatest: 0, pinned: 0, behind: 0, total: states.length }
  for (const state of states) rollup[versionBucket(state, newestId)] += 1
  return rollup
}

/**
 * What a roster row's Version cell says, as a value rather than as markup: the version, whether it is
 * a pin, and whether it is behind the newest release.
 *
 * **The pin wins over what is deployed**, matching the stack header invariant: a pinned row states the
 * version it is on or converging onto, which is the version its Deploy would apply. A latest-tracking
 * row has no such commitment, so it states what it last deployed.
 *
 * `version` is null for a row that has never deployed anything and carries no pin — "—" rather than an
 * invented answer. It deliberately does *not* fall back to "latest" there: a stack that has not
 * deployed the newest release is not on it.
 */
export function rosterVersion(state: VersionState, newestId: number | null): {
  version: string | null
  pinned: boolean
  behind: boolean
} {
  const pin = state.pinnedRelease
  const running = state.lastDeployedRelease
  const current = pin ?? running
  return {
    version: current?.version ?? null,
    pinned: pin != null,
    // `>` rather than `!==` (invariant 7): a row ahead of the window's idea of newest is not behind.
    behind: newestId != null && current != null && newestId > current.id,
  }
}
