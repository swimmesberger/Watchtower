import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'

/**
 * How many releases the pin pickers offer, and the window "N behind" is exact within.
 *
 * The Releases tab pages properly; every picker is this fixed window, so a pin to a release older
 * than 20 can be read (the chip names it) but not re-selected from a dropdown. Widen this, or give the
 * pickers their own paging, if a fleet ever needs it.
 */
export const RELEASE_OPTIONS = 20

/**
 * The product's newest releases, newest first — **one query key across the app**.
 *
 * It lives here rather than in a module because three modules read it: stacks (the Version dialog and
 * panel), products (the Releases tab's row actions) and templates (the Instances rollup and the fleet
 * roll-out dialog), and modules never import each other. One key means react-query dedupes all of them
 * into a single request per product, and a release list fetched by one surface is the same list the
 * next one reads.
 *
 * Deliberately not the Releases tab's infinite query: that one pages, this is a fixed window.
 */
export function useProductReleases(productId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['product', productId, 'releases', 'options'],
    queryFn: () => api.products.listReleases(productId, undefined, RELEASE_OPTIONS),
    enabled,
  })
}
