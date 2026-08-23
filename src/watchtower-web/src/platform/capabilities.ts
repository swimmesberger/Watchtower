// The capability snapshot resolution reads — fetched once at boot from the backend's `elarion.session`
// bootstrap (ADR-0030) and wrapped in the generated typed accessors: which modules are enabled, the
// [ClientFeatures] flags the deployment resolves (e.g. `metrics-history` — true on the database and
// influxdb metrics backends, ADR-0013; a runtime backend switch reloads the page to re-fetch), and who
// the caller is.
//
// With `Auth:Enabled` the snapshot carries the signed-in account (and is unauthenticated before login,
// which is what the router's login guard reads); without it the backend reports an implicit local
// administrator, so nothing downstream changes.
//
// This is a read-only UX projection, not an enforcement boundary — hiding a nav item secures nothing.
import {
  createSessionCapabilities,
  type ClientSnapshot,
  type SessionCapabilities,
} from '@/generated/session-client'
import { rpc } from '@/lib/rpc-client'

// Fail closed: when the API is unreachable the shell still renders, with every gated contribution hidden.
// `isAuthenticated: false` also routes the visitor to the login page rather than a half-dead dashboard.
const OFFLINE: ClientSnapshot = {
  user: { id: '', isAuthenticated: false, roles: [], permissions: [] },
  modules: {},
  flags: {},
  variants: {},
}

/**
 * Fetches the boot snapshot; called once in `main.tsx` before the contribution registry is built.
 * Returns the concrete `SessionCapabilities` rather than the narrower `CapabilityReader` the contribution
 * kernel needs, because the router and the shell also read `.user` off it.
 */
export async function loadCapabilities(): Promise<SessionCapabilities> {
  try {
    const snapshot = (await rpc('elarion.session', {})) as ClientSnapshot
    return createSessionCapabilities(snapshot)
  } catch (error) {
    console.error('Failed to load the capability snapshot — rendering with everything off.', error)
    return createSessionCapabilities(OFFLINE)
  }
}
