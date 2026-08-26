import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRouteContext } from '@tanstack/react-router'
import { api } from '@/lib/api'
import type { ProductStack, StackTemplate } from '@/lib/types'
import { SYSTEM_REALM_ID } from '@/hooks/use-realms'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { toast } from '@/components/ui/use-toast'

// ── Adopt an existing stack (ADR-0026 stage 9) ───────────────────────────────────
//
// The counterpart to the add-tenant row: that one *creates* an instance, this one takes one that is
// already running and gives it the setup's identity. The whole reason it exists is that the stack keeps
// running throughout — same containers, same volumes, same data, same name, same compose project — so
// the dialog's job is to say exactly that before the click, not afterwards.
//
// It lives beside `InstancesTab` rather than in `components/` because, unlike the roll-out dialog, only
// one screen opens it.

/**
 * The consequence sentence — the one thing this dialog owes the reader.
 *
 * Two halves, and both matter. What *will* happen ("acme.example.com will point at web:3000") and what
 * will *not* ("the stack keeps its name, project, environment and version"), because every fear an
 * operator has about pointing Watchtower's tenancy machinery at a production stack is in the second
 * half. The backup clause is separate and only rendered where it is true: policy is the one thing that
 * does start following the fleet, through the tri-state columns, and saying so is cheaper than having
 * it discovered.
 */
function consequence(domain: string, service: string, port: number): string {
  return `${domain} will point at ${service}:${port}. The stack keeps its name, project, environment and version.`
}

export function AdoptStackDialog({
  open,
  onOpenChange,
  productId,
  template,
  stacks,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  productId: number
  template: StackTemplate
  /** The product's standalone stacks — the only ones that can be adopted. */
  stacks: ProductStack[]
}) {
  const qc = useQueryClient()
  const { caps } = useRouteContext({ from: '__root__' })
  const backupsEnabled = caps.isModuleEnabled('Backups')
  const [stackId, setStackId] = useState('')
  const [slug, setSlug] = useState('')
  const [error, setError] = useState<string | null>(null)

  // A refetch can take the picked stack out of the list (someone adopted it, or deleted it), so clear
  // the selection rather than let Adopt submit a choice the backend would refuse.
  const stackKey = stacks.map((s) => s.id).join(',')
  useEffect(() => {
    if (stackId && !stackKey.split(',').includes(stackId)) setStackId('')
  }, [stackId, stackKey])

  // Reopening starts clean: a slug left over from a refused attempt is the kind of thing that gets
  // applied to a different stack the second time round.
  useEffect(() => {
    if (!open) {
      setStackId('')
      setSlug('')
      setError(null)
    }
  }, [open])

  /**
   * The realm line, and why it is not gated on `realms.list`.
   *
   * A service route takes its realm from its stack's category, so adopting into a non-system setup moves
   * which population is admitted through the stack's domains. The backend *refuses* that outright when
   * any of them is protected (`AccessMode !== 'public'`), so everything that reaches this dialog is the
   * allowed case — and the allowed case still deserves the affirmative sentence, because the stack's
   * future domains, and any protection added later, will be the setup's realm's. It reads
   * `template.realmName` rather than the Admin-only realm roster: a non-administrator can adopt a stack
   * and must be able to read what they are agreeing to.
   */
  const realmName =
    template.realmId !== SYSTEM_REALM_ID ? template.realmName : null

  const trimmedSlug = slug.trim()
  const domain = template.domainPattern.replace('{tenant}', trimmedSlug || 'slug')
  const picked = stacks.find((s) => String(s.id) === stackId)

  const adopt = useMutation({
    mutationFn: () => api.templates.adoptStack(template.id, Number(stackId), trimmedSlug),
    onSuccess: (result) => {
      const added = result.envKeysAdded ?? []
      toast.success(
        `${result.tenant.stackName} is now the tenant ${result.tenant.tenantSlug}.`,
        [
          result.domainIsPrimary
            ? `${result.domain} is its primary domain.`
            : `${result.domain} was added; ${result.tenant.domain} is still its primary domain.`,
          added.length > 0
            ? `${added.length} base environment variable${added.length === 1 ? '' : 's'} added: ${added.join(', ')}.`
            : 'It already defined every base environment variable, so none were added.',
          'Nothing was redeployed — the stack is running exactly as it was.',
        ].join(' '),
      )
      onOpenChange(false)
      // The stack moved from the product's standalone list into this setup's roster; both surfaces,
      // plus the route list and the dashboard's fleet cards, read it.
      qc.invalidateQueries({ queryKey: ['tenants', template.id] })
      qc.invalidateQueries({ queryKey: ['template', template.id] })
      qc.invalidateQueries({ queryKey: ['product', productId] })
      qc.invalidateQueries({ queryKey: ['products'] })
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['routes'] })
    },
    // Verbatim: every refusal names the thing that is in the way, which is the only actionable part.
    onError: (err: Error) => setError(err.message),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Adopt an existing stack</DialogTitle>
          <DialogDescription>
            Makes a stack that is already running a tenant of {template.name}. It keeps its containers,
            volumes and data — nothing is recreated and nothing is redeployed.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <Field
            label="Stack"
            required
            hint={
              stacks.length === 0
                ? 'Every deployment of this product is already a tenant.'
                : 'Only this product’s standalone deployments can be adopted.'
            }
          >
            {({ id, describedBy }) => (
              <Select value={stackId} onValueChange={setStackId}>
                <SelectTrigger id={id} aria-describedby={describedBy} disabled={stacks.length === 0}>
                  <SelectValue
                    placeholder={stacks.length === 0 ? 'Nothing to adopt' : 'Select a stack'}
                  />
                </SelectTrigger>
                <SelectContent>
                  {stacks.map((s) => (
                    <SelectItem key={s.id} value={String(s.id)}>
                      {s.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </Field>

          {/* The add-tenant row's live domain preview, verbatim — the same control answering the same
              question, so a reader who has used one has used the other. */}
          <Field label="Tenant slug" required hint={domain}>
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                mono
                value={slug}
                onChange={(e) => setSlug(e.target.value)}
                placeholder="tenant1"
                autoComplete="off"
                spellCheck={false}
              />
            )}
          </Field>

          <div className="space-y-1.5 text-[13px] text-text-2">
            <p>
              {picked && trimmedSlug
                ? consequence(domain, template.targetServiceName, template.targetPort)
                : 'Pick a stack and a slug to see what will change.'}
            </p>
            {picked && trimmedSlug && backupsEnabled && (
              <p>
                Its backup policy starts following {template.name}’s for every setting it has not set
                itself.
              </p>
            )}
            {picked && trimmedSlug && realmName && (
              <p>Its domains join the {realmName} realm — that is the population admitted to them.</p>
            )}
          </div>

          {error && (
            <Banner tone="danger" title="Couldn’t adopt the stack">
              {error}
            </Banner>
          )}
        </div>

        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            loading={adopt.isPending}
            disabled={adopt.isPending || !stackId || !trimmedSlug}
            onClick={() => {
              setError(null)
              adopt.mutate()
            }}
          >
            Adopt
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
