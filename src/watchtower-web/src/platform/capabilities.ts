// The capability snapshot resolution reads. Watchtower ships every module and has no authentication, so
// modules/permissions/roles are all enabled (flags default off — there are none). `createStaticCapabilities`
// is the no-session reader the framework ships for exactly this self-hosted/no-auth case (Elarion #71); if
// a backend capabilities/session endpoint is ever added, swap in the generated `SessionCapabilities` (same
// structural interface, nothing else changes).
import { createStaticCapabilities } from '@swimmesberger/elarion-contributions'

export const capabilities = createStaticCapabilities()
