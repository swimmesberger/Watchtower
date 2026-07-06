// The capability snapshot resolution reads — fetched once at boot from the backend's `elarion.session`
// bootstrap (ADR-0030) and wrapped in the generated typed accessors. Watchtower is unauthenticated, so the
// snapshot is the anonymous one: every shipped module enabled, no grants, plus the [ClientFeatures] flags
// the deployment resolves (e.g. `metrics-history` — true only on the InfluxDB metrics backend, ADR-0007).
//
// This is a read-only UX projection, not an enforcement boundary — hiding a nav item secures nothing.
import {
  createSessionCapabilities,
  type ClientSnapshot,
} from '@/generated/session-client'
import type { CapabilityReader } from '@swimmesberger/elarion-contributions'
import { rpc } from '@/lib/rpc-client'

// Fail closed: when the API is unreachable the shell still renders, with every gated contribution hidden.
const OFFLINE: ClientSnapshot = {
  user: { id: '', isAuthenticated: false, roles: [], permissions: [] },
  modules: {},
  flags: {},
  variants: {},
}

/** Fetches the boot snapshot; called once in `main.tsx` before the contribution registry is built. */
export async function loadCapabilities(): Promise<CapabilityReader> {
  try {
    const snapshot = (await rpc('elarion.session', {})) as ClientSnapshot
    return createSessionCapabilities(snapshot)
  } catch (error) {
    console.error('Failed to load the capability snapshot — rendering with everything off.', error)
    return createSessionCapabilities(OFFLINE)
  }
}
