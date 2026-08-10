// The capability vocabulary the contribution kit is typed against (ADR-0032). The literal unions come
// from the generated `session-client.ts` — lowered from the exported schema's `capabilities` block, which
// the backend resolves from its own [AppModule]/[ClientFeatures] declarations — so a typo'd module or flag
// in a `when` clause is a compile error checked against the same catalog the backend enforces.
//
// The `role` axis is wired because handlers now declare [RequireRole] (the Users module) and the
// design's §8 wants the matching UI gated on the role as well as the module. Note the asymmetry with
// `module`/`flag`: the backend exports no role catalogue, so the generated `RoleName` is plain `string`
// and a typo'd role is NOT a compile error — it silently hides the contribution. Keep role clauses to
// the constants the backend actually mints (`WatchtowerClaims.AdminRole`), and treat the axis as a UX
// projection: the enforcement is [RequireRole] on the handler, never this.
//
// The permission axis stays omitted: the kit reduces an omitted axis to `never`, making a stray
// `permission` clause a compile error instead of a silently accepted string (Elarion #71). Nothing
// declares [RequirePermission], so there is no vocabulary behind it yet.
import type { ModuleName, FlagName, RoleName } from '@/generated/session-client'

export type { ModuleName, FlagName, RoleName }

export interface AppVocabulary {
  module: ModuleName
  flag: FlagName
  role: RoleName
}
