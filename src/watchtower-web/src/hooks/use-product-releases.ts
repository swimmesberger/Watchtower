import { useMemo } from 'react'
import { useInfiniteQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import type { ReleasePage } from '@/lib/types'

/**
 * How many releases a pin picker loads at a time, and the window "N behind" is exact within.
 *
 * It is a page size now, not a ceiling: the pickers page with "Show older" (see below), so a pin to a
 * release older than the first page can be reached by loading down to it.
 */
export const RELEASE_OPTIONS = 20

/**
 * The product's releases, newest first — **one query key across the app**.
 *
 * It lives here rather than in a module because three modules read it: stacks (the Version dialog and
 * panel), products (the Releases tab's row actions) and templates (the Instances rollup and the fleet
 * roll-out dialog), and modules never import each other. One key means react-query dedupes all of them
 * into a single request per product, and a release list fetched by one surface is the same list the
 * next one reads.
 *
 * **Keyset-paged, and the first page is the whole story for almost every caller.** The rollups, chips
 * and "what is newest" derivations only ever read `releases[0]` and the ids around it, so they behave
 * exactly as they did when this was a fixed 20-row window. The two *pickers* call `showOlder` to load
 * further pages, which is what makes a pin older than the newest 20 selectable again rather than merely
 * readable. Paging on the id, not an offset: a release published while somebody pages cannot shift the
 * window.
 *
 * Deliberately still not the Releases tab's own infinite query — that one is the tab's list state, with
 * its own filters and its own page size; this one is the shared option list.
 */
export function useProductReleases(productId: number, enabled: boolean) {
  const query = useInfiniteQuery({
    queryKey: ['product', productId, 'releases', 'options'],
    queryFn: ({ pageParam }) => api.products.listReleases(productId, pageParam, RELEASE_OPTIONS),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) =>
      last.hasMore ? last.releases[last.releases.length - 1]?.id : undefined,
    enabled,
  })

  const releases = useMemo(
    () => query.data?.pages.flatMap((p) => p.releases) ?? [],
    [query.data],
  )

  return {
    ...query,
    /**
     * The loaded releases as one page, so every caller that only ever read `data.releases` keeps
     * reading it — and keeps its "has the list arrived yet?" check, which is `data != null`.
     */
    data: query.data ? ({ releases, hasMore: query.hasNextPage } satisfies ReleasePage) : undefined,
    /** Loads the next older page. The pickers' "Show older"; nothing else calls it. */
    showOlder: query.fetchNextPage,
    hasOlder: query.hasNextPage,
    loadingOlder: query.isFetchingNextPage,
  }
}
