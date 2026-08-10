import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'

/**
 * The built-in operator realm's id. Seeded by the migration that introduced realms and never re-issued,
 * so it is safe to name — every screen that has to pick a default population before `realms.list` has
 * answered uses it, and `useRealms().systemRealmId` prefers the loaded row once one is available.
 */
export const SYSTEM_REALM_ID = 1

/** The value a realm `<Select>` uses for "every realm" — Radix Select rejects an empty-string item value. */
export const ALL_REALMS = 'all'

/**
 * The realm roster, shared by every screen that has to name one. One query key (`['realms']`) across the
 * app so the Users, Groups and Templates screens read the same cached roster the Realms screen writes,
 * and a realm renamed there is renamed everywhere without a reload.
 *
 * `realms.list` carries `[RequireRole("Admin")]`. Screens that are themselves Admin-gated can call this
 * unconditionally; a screen that is not (Templates is gated on the module only) must pass
 * `{ enabled: caps.hasRole('Admin') }` so a non-administrator does not fetch a Forbidden it cannot use.
 * With the query off the roster is simply empty, and every realm-aware control degrades to absent.
 */
export function useRealms(options?: { enabled?: boolean }) {
  const query = useQuery({
    queryKey: ['realms'],
    queryFn: api.realms.list,
    enabled: options?.enabled ?? true,
  })

  const realms = query.data ?? []
  const byId = new Map(realms.map((r) => [r.id, r]))

  return {
    realms,
    /** The realm's name, or a placeholder naming the id when the roster hasn't loaded (or it is gone). */
    nameOf: (realmId: number) => byId.get(realmId)?.name ?? `Realm ${realmId}`,
    /** The realm's name, or null when the roster carries no answer — for copy that is better left out. */
    nameOrNull: (realmId: number) => byId.get(realmId)?.name ?? null,
    /** Where a new user, group or template goes unless told otherwise. */
    systemRealmId: realms.find((r) => r.isSystem)?.id ?? SYSTEM_REALM_ID,
    isSystem: (realmId: number) => byId.get(realmId)?.isSystem ?? realmId === SYSTEM_REALM_ID,
  }
}
