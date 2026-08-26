import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Play } from 'lucide-react'
import { api } from '@/lib/api'
import type { Product } from '@/lib/types'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { toast } from '@/components/ui/use-toast'

/**
 * "Deploy latest" / "Deploy latest to all" — the product-scoped roll-out (`products.deployRelease`).
 *
 * Deliberately not a per-row action on the Releases tab: deploying *an arbitrary* release to an
 * instance is a pin, and a pin is stack-scoped (`stacks.setRelease`, from the stack's Version dialog).
 * What a product can do to its whole fleet at once is move it onto the newest release, so that is the
 * one button, and it lives in the header where its scope is legible.
 *
 * The confirm dialog states the target set exactly, because the backend's predicate is not "every
 * instance": pinned stacks are standing instructions and stopped ones are disabled, and both are
 * skipped. The release id is sent as the staleness guard — if CI published something newer while the
 * dialog was open the call is refused with a `409`, shown verbatim.
 */
export function DeployLatestButton({
  product,
  label = 'Deploy latest',
  size,
  variant = 'primary',
}: {
  product: Product
  label?: string
  size?: 'sm' | 'md'
  variant?: 'primary' | 'secondary'
}) {
  const qc = useQueryClient()
  const [open, setOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const latest = product.latestRelease

  const rollout = useMutation({
    mutationFn: () => api.products.deployRelease(product.id, latest?.id ?? null),
    onSuccess: (result) => {
      qc.invalidateQueries({ queryKey: ['product', product.id] })
      // A prefix, deliberately: the roll-out names deploy *event* ids, not stack ids, so there is no
      // list of stacks to invalidate one by one. `['stacks']` matches every key under it — the list,
      // each `['stacks', id]` and each `['stacks', id, 'events']` history — which is exactly the set
      // a fan-out just changed.
      qc.invalidateQueries({ queryKey: ['stacks'] })
      setOpen(false)
      if (result.stacksEnqueued === 0)
        toast.info(
          `Nothing to deploy for ${result.version}.`,
          'No instance is both tracking latest and running.',
        )
      else
        toast.info(
          `Deploying ${result.version} to ${result.stacksEnqueued} instance${
            result.stacksEnqueued === 1 ? '' : 's'
          }…`,
        )
    },
    // Verbatim: the refusal names the release that is newer, or the mode that has no releases to roll.
    onError: (err: Error) => setError(err.message),
  })

  if (!latest) return null

  return (
    <>
      <Button
        variant={variant}
        size={size}
        onClick={() => {
          setError(null)
          setOpen(true)
        }}
      >
        <Play /> {label}
      </Button>

      <ConfirmDialog
        open={open}
        onOpenChange={(next) => {
          if (!next) setError(null)
          setOpen(next)
        }}
        title={`Deploy ${latest.version} to all instances?`}
        description={`Deploys ${latest.version} to every instance tracking latest — pinned and stopped instances are skipped.`}
        extra={
          error && (
            <Banner tone="danger" title="Couldn’t roll this release out">
              {error}
            </Banner>
          )
        }
        confirmLabel="Deploy to all"
        loading={rollout.isPending}
        onConfirm={() => {
          setError(null)
          rollout.mutate()
        }}
      />
    </>
  )
}
