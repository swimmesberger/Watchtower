// Helpers for the dynamically-composed (loose) router. Because the route tree is assembled at runtime
// from discovered modules, TanStack can't statically type navigation options — so `useNavigate()`'s
// `search`/`params` degrade. These give call sites a small, honest typed surface instead of `any`.
export interface LooseNavigateOptions {
  to?: string
  params?: Record<string, unknown> | ((prev: Record<string, unknown>) => Record<string, unknown>)
  search?: Record<string, unknown> | ((prev: Record<string, unknown>) => Record<string, unknown>)
  replace?: boolean
  hash?: string
}

export type LooseNavigate = (opts: LooseNavigateOptions) => void
