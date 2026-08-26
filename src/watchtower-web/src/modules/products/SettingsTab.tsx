import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { api } from '@/lib/api'
import type { Product, ReleaseMode } from '@/lib/types'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { SectionHeader } from '@/components/ui/section-header'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { toast } from '@/components/ui/use-toast'

const NO_CREDENTIAL = 'none'

export function SettingsTab({ product }: { product: Product }) {
  const qc = useQueryClient()
  const navigate = useNavigate()

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const [form, setForm] = useState({
    name: product.name,
    description: product.description ?? '',
    repositoryUrl: product.repositoryUrl,
    defaultBranch: product.defaultBranch,
    composeFilePath: product.composeFilePath,
    credentialId: product.credentialId,
    releaseMode: product.releaseMode as ReleaseMode,
  })
  const [error, setError] = useState<string | null>(null)
  const [confirmSource, setConfirmSource] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  /**
   * Whether the operator has actually moved the update-mechanism select in this session.
   *
   * **The gate is intent, not a diff.** `form` is seeded once at mount and this component never
   * remounts on a refetch, so a snapshot-vs-live comparison silently becomes true the moment the mode
   * changes *behind* the page — which it does on its own, every time a release is published and flips
   * `Git → Releases`. A save of some unrelated field would then post the stale mount value and revert
   * the flip, with nothing but an unprompted warning banner to show for it. Nothing the reader did
   * asked for that, so nothing the reader did should send it.
   *
   * Deliberately not fixed by re-seeding `form` from the refetched product in an effect: that would
   * clobber a selection the operator is in the middle of making. `selectedMode` below derives the
   * displayed value from this flag instead, which cannot race anything.
   */
  const [modeTouched, setModeTouched] = useState(false)

  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) =>
    setForm((prev) => ({ ...prev, [key]: value }))

  // Everything a deployment clones from. Editing any of these reaches every stack of the product at
  // its next deploy, which is what the confirmation below spells out.
  const sourceChanged =
    form.repositoryUrl.trim() !== product.repositoryUrl ||
    form.defaultBranch.trim() !== product.defaultBranch ||
    form.composeFilePath.trim() !== product.composeFilePath ||
    form.credentialId !== product.credentialId

  // What the select shows: the operator's choice once they have made one, otherwise whatever the
  // product currently says. Derived rather than re-seeded through an effect — an effect could race a
  // selection in progress, and this cannot, because the branch *is* "the operator has not chosen".
  // Without it the control would keep showing the mount-time value over a product whose mode moved
  // behind the page, so a reader could not tell the two apart and picking Git on an
  // already-showing-Git control would be a no-op that reverts nothing.
  const selectedMode = modeTouched ? form.releaseMode : (product.releaseMode as ReleaseMode)
  // The gate on sending the field at all (see the mutation) and on warning about the revert: the
  // operator moved the select *and* it now says something other than the product does.
  const modeChanged = modeTouched && selectedMode !== product.releaseMode
  // Not merely "the select says git": a product already in Git mode is not reverting anything.
  const revertingToGit = modeChanged && selectedMode === 'git'

  // Both kinds block a delete and both take the new source at their next deploy, so both are
  // counted and both are named — a count that only mentioned stacks would understate the blast
  // radius of a save and misdescribe the refusal a delete is about to hit.
  const usageCount = product.stackCount + product.templateCount
  const usagePhrase = [
    product.stackCount > 0 && `${product.stackCount} deployment${product.stackCount === 1 ? '' : 's'}`,
    product.templateCount > 0 && `${product.templateCount} template${product.templateCount === 1 ? '' : 's'}`,
  ]
    .filter(Boolean)
    .join(' and ')

  const save = useMutation({
    mutationFn: () =>
      api.products.update(product.id, {
        name: form.name,
        description: form.description.trim() === '' ? null : form.description,
        repositoryUrl: form.repositoryUrl,
        composeFilePath: form.composeFilePath,
        defaultBranch: form.defaultBranch,
        credentialId: form.credentialId,
        // **Only when the operator actually moved the select.** The field means "leave it alone" when
        // null, and sending the value this page loaded with would make every unrelated save — a
        // rename, a description edit — a mode write. The mode is also flipped from outside this form
        // (the first release published flips Git → Releases), so a stale value posted back by a save
        // minutes later would silently revert a flip that had already landed.
        releaseMode: modeChanged ? selectedMode : null,
      }),
    onSuccess: (updated) => {
      qc.invalidateQueries({ queryKey: ['product', product.id] })
      qc.invalidateQueries({ queryKey: ['products'] })
      // Every stack and template of this product projects its source, so their names and branches
      // just changed too.
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['templates'] })
      // An open template detail reads ['template', id], which the plural key above does not cover —
      // and its "From product …" line is exactly what this save just changed.
      qc.invalidateQueries({ queryKey: ['template'] })
      setError(null)
      toast.success(`Saved ${updated.name}.`)
    },
    // Surfaced verbatim: the backend's refusals name the conflicting product and what to do about
    // it, which no generic message here could improve on.
    onError: (err: Error) => {
      setError(err.message)
      toast.error('Save failed', err.message)
    },
    onSettled: () => setConfirmSource(false),
  })

  const remove = useMutation({
    mutationFn: () => api.products.delete(product.id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['products'] })
      toast.success(`Deleted ${product.name}.`)
      navigate({ to: '/products' })
    },
    // products.delete refuses with the stacks and templates that block it, by name.
    onError: (err: Error) => toast.error('Delete failed', err.message),
    onSettled: () => setConfirmDelete(false),
  })

  function handleSave(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    if (sourceChanged && usageCount > 0) {
      setConfirmSource(true)
      return
    }
    save.mutate()
  }

  return (
    <form onSubmit={handleSave} className="max-w-2xl space-y-8">
      <section>
        <SectionHeader title="Product" description="How this product is named in the catalogue." />
        <Card>
          <CardContent className="space-y-4">
            <Field label="Name" required>
              {({ id }) => (
                <Input
                  id={id}
                  value={form.name}
                  onChange={(e) => set('name', e.target.value)}
                  required
                />
              )}
            </Field>
            <Field label="Description" hint="Optional. One line, for the catalogue.">
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  value={form.description}
                  onChange={(e) => set('description', e.target.value)}
                  placeholder="The customer-facing web app"
                />
              )}
            </Field>
          </CardContent>
        </Card>
      </section>

      <section>
        <SectionHeader
          title="Source"
          description="Where every deployment of this product is cloned from."
          action={
            usageCount > 0 ? (
              <span className="text-[13px] text-text-2">Used by {usagePhrase}</span>
            ) : undefined
          }
        />
        <Card>
          <CardContent className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Field label="Repository URL" required className="md:col-span-2">
              {({ id }) => (
                <Input
                  id={id}
                  mono
                  value={form.repositoryUrl}
                  onChange={(e) => set('repositoryUrl', e.target.value)}
                  placeholder="https://github.com/owner/repo"
                  required
                  spellCheck={false}
                />
              )}
            </Field>

            <Field label="Branch" hint="Deployed unless a stack overrides it">
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  mono
                  value={form.defaultBranch}
                  onChange={(e) => set('defaultBranch', e.target.value)}
                  placeholder="main"
                  required
                  spellCheck={false}
                />
              )}
            </Field>

            <Field label="Compose file path" hint="Relative to the repo root">
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  mono
                  value={form.composeFilePath}
                  onChange={(e) => set('composeFilePath', e.target.value)}
                  placeholder="docker-compose.yml"
                  required
                  spellCheck={false}
                />
              )}
            </Field>

            <Field
              label="Credential"
              hint="Only needed for private repositories"
              className="md:col-span-2"
            >
              {({ id, describedBy }) => (
                <Select
                  value={form.credentialId != null ? String(form.credentialId) : NO_CREDENTIAL}
                  onValueChange={(v) => set('credentialId', v === NO_CREDENTIAL ? null : Number(v))}
                >
                  <SelectTrigger id={id} aria-describedby={describedBy}>
                    <SelectValue placeholder="None (public repository)" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value={NO_CREDENTIAL}>None (public repository)</SelectItem>
                    {credentials.map((c) => (
                      <SelectItem key={c.id} value={String(c.id)}>
                        {c.name} ({c.username})
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            </Field>
          </CardContent>
        </Card>
      </section>

      {/* The mode-revert control, owed since stage 4b. Rendered only once the product has releases:
          `releases` is refused for a product with none, so a select offering it before then would be a
          control whose only other option fails. The switch is normally flipped by the first release —
          this is the way back, and the warning is why it is not a quiet dropdown. */}
      {product.latestRelease && (
        <section>
          <SectionHeader
            title="Updates"
            description="How this product's deployments learn there is something new."
          />
          <Card>
            <CardContent className="space-y-3">
              <Field
                label="Update mechanism"
                hint="Switched automatically by the first release; change it back here."
              >
                {({ id, describedBy }) => (
                  <Select
                    value={selectedMode}
                    onValueChange={(v) => {
                      setModeTouched(true)
                      set('releaseMode', v as ReleaseMode)
                    }}
                  >
                    <SelectTrigger id={id} aria-describedby={describedBy} className="md:w-80">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="releases">Releases — deploy a build your CI reported</SelectItem>
                      <SelectItem value="git">Git — deploy the branch head</SelectItem>
                    </SelectContent>
                  </Select>
                )}
              </Field>
              {/* Stated before the save, not after: what makes a revert surprising is that the pins
                  survive it and stop meaning anything, which nothing on screen would otherwise say. */}
              {revertingToGit && (
                <Banner tone="warn" title="Deployments go back to branch-head deploys">
                  Every deployment of {product.name} will clone{' '}
                  <span className="font-mono">{product.defaultBranch}</span> again instead of a
                  release. Pinned deployments keep their pin, but it becomes inert — nothing reads it
                  in Git mode. The next release published switches this back automatically.
                </Banner>
              )}
            </CardContent>
          </Card>
        </section>
      )}

      {error && (
        <Banner tone="danger" title="Could not save this product">
          {error}
        </Banner>
      )}

      <div className="flex items-center gap-3">
        <Button type="submit" variant="primary" loading={save.isPending}>
          Save product
        </Button>
      </div>

      <section>
        <SectionHeader title="Danger zone" />
        <Card className="border-danger-bd">
          <CardContent className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="min-w-0">
              <p className="text-sm font-medium text-text">Delete product</p>
              <p className="mt-0.5 text-[13px] text-text-2">
                Only possible while no stack and no template uses this product.
              </p>
            </div>
            <Button
              type="button"
              variant="danger"
              className="shrink-0"
              onClick={() => setConfirmDelete(true)}
            >
              Delete product
            </Button>
          </CardContent>
        </Card>
      </section>

      <ConfirmDialog
        open={confirmSource}
        onOpenChange={(open) => {
          if (!open && !save.isPending) setConfirmSource(false)
        }}
        title="Change the source for everything using it?"
        description={`Saving changes the source for ${usagePhrase}. They keep running until redeployed.`}
        confirmLabel="Save changes"
        loading={save.isPending}
        onConfirm={() => save.mutate()}
      />

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setConfirmDelete(false)
        }}
        title={`Delete ${product.name}?`}
        description="The catalogue entry is removed. Nothing else is deleted."
        confirmLabel="Delete product"
        tone="danger"
        requireText={product.name}
        loading={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </form>
  )
}
