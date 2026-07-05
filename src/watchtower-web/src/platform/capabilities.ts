// The capability snapshot resolution reads. Watchtower ships every module and has no authentication,
// so every module is enabled and every auth-gated check is satisfied. If a backend capabilities/session
// endpoint is ever added, replace this with a reader built from it (the interface is what the Elarion
// client generator's `SessionCapabilities` class structurally satisfies).
import type { CapabilityReader } from '@swimmesberger/elarion-contributions'
import type { ModuleName } from './vocabulary'

const enabledModules = new Set<ModuleName>([
  'Credentials',
  'Registries',
  'Stacks',
  'Deployments',
  'Containers',
  'System',
  'Volumes',
  'Networks',
  'Metrics',
])

export const capabilities: CapabilityReader = {
  isModuleEnabled: (name) => enabledModules.has(name as ModuleName),
  hasPermission: () => true,
  hasRole: () => true,
  isFlagEnabled: () => true,
}
