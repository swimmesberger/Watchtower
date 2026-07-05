// The capability vocabulary the contribution kit is typed against. In a fully-featured Elarion app
// these unions are generated into `session-client.ts` (ModuleName/PermissionName/FlagName/RoleName).
// Watchtower has no authentication (it runs behind an authenticating reverse proxy — see README) and
// no session-client generator, so we hand-author the module vocabulary to mirror the backend
// `[AppModule]` names and omit the auth axes entirely. The kit's `Vocabulary` axes are optional, so an
// omitted axis reduces its `when` type to `never` — a stray `permission`/`flag`/`role` clause is a
// compile error here, not a silently accepted string (Elarion #71).

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
  // permission/flag/role are intentionally omitted — Watchtower has no auth model, and omitting them
  // makes any use of those axes in a `when` clause a compile error.
}
