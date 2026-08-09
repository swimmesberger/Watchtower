// The capability vocabulary the contribution kit is typed against (ADR-0032). The literal unions come
// from the generated `session-client.ts` — lowered from the exported schema's `capabilities` block, which
// the backend resolves from its own [AppModule]/[ClientFeatures] declarations — so a typo'd module or flag
// in a `when` clause is a compile error checked against the same catalog the backend enforces.
//
// The permission/role axes stay omitted: the kit reduces an omitted axis to `never`, making a stray
// `permission`/`role` clause a compile error instead of a silently accepted string (Elarion #71). No
// handler declares [RequireRole]/[RequirePermission] yet, so the exported schema carries no such
// vocabulary — role-gated contributions arrive with the role gating itself.
import type { ModuleName, FlagName } from '@/generated/session-client'

export type { ModuleName, FlagName }

export interface AppVocabulary {
  module: ModuleName
  flag: FlagName
}
