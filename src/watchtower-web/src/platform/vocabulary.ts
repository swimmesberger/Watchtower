// The capability vocabulary the contribution kit is typed against. In a fully-featured Elarion app
// these unions are generated into `session-client.ts` (ModuleName/PermissionName/FlagName/RoleName).
// Watchtower has no authentication (it runs behind an authenticating reverse proxy — see README) and
// no session-client generator, so we hand-author the module vocabulary to mirror the backend
// `[AppModule]` names and leave the auth axes empty.

/** Mirrors the backend Elarion module names (the `[AppModule("…")]` markers). */
export type ModuleName =
  | 'Credentials'
  | 'Registries'
  | 'Stacks'
  | 'Deployments'
  | 'Containers'
  | 'System'
  | 'Volumes'
  | 'Networks'
  | 'Metrics'

export interface AppVocabulary {
  module: ModuleName
  // Watchtower has no permission/role/flag model; these axes are intentionally empty.
  permission: never
  flag: never
  role: never
}
