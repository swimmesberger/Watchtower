import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getRouteApi, useNavigate } from '@tanstack/react-router'
import { api } from '@/lib/api'
import { Spinner } from '@/components/ui/spinner'

const routeApi = getRouteApi('/templates/$id')

/**
 * Where a `/templates/$id` bookmark lands after the fold: the product's Instances tab, which is the
 * screen that page became (design.md §Navigation).
 *
 * **A component rather than a `beforeLoad` redirect, and the difference is not cosmetic.** The product
 * id has to be looked up, so the guard would have to be asynchronous — and an async `beforeLoad` whose
 * lookup *rejects* leaves the router with no match and renders a blank page, which is what a
 * deleted-template bookmark reaches. Resolving in a component keeps every outcome on screen: a spinner
 * while it looks, the Instances tab when it finds one, and the catalogue when it does not.
 *
 * The query key is the one the Instances tab reads, so the hop costs one request, not two.
 */
export function TemplateRedirect() {
  const { id } = routeApi.useParams()
  const templateId = Number(id)
  const navigate = useNavigate()

  const { data, isError } = useQuery({
    queryKey: ['template', templateId],
    queryFn: () => api.templates.get(templateId),
    enabled: Number.isInteger(templateId),
    // One shot: a template that is gone is gone, and retrying only lengthens the blank moment.
    retry: false,
  })

  const productId = data?.template.productId
  // A template that no longer exists (or an id that never was one) goes to the catalogue rather than
  // to a "not found" page — a stale bookmark should land somewhere real. **A template that answered
  // without a product id counts as lost too**: `productId` is required on the wire, so a nullish one
  // means something upstream is wrong, and the alternative to a terminal state here is a spinner that
  // never stops.
  const lost = isError || !Number.isInteger(templateId) || (data != null && productId == null)

  useEffect(() => {
    if (productId != null) {
      navigate({
        to: '/products/$id',
        params: { id: String(productId) },
        search: { tab: 'instances' },
        replace: true,
      })
    } else if (lost) {
      navigate({ to: '/products', replace: true })
    }
  }, [productId, lost, navigate])

  return (
    <div className="flex justify-center p-10">
      <Spinner />
    </div>
  )
}
