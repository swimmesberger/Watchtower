import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { api } from '@/lib/api'
import type { Product } from '@/lib/types'
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
  })
  const [error, setError] = useState<string | null>(null)
  const [confirmSource, setConfirmSource] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) =>
    setForm((prev) => ({ ...prev, [key]: value }))

  // Everything a deployment clones from. Editing any of these reaches every stack of the product at
  // its next deploy, which is what the confirmation below spells out.
  const sourceChanged =
    form.repositoryUrl.trim() !== product.repositoryUrl ||
    form.defaultBranch.trim() !== product.defaultBranch ||
    form.composeFilePath.trim() !== product.composeFilePath ||
    form.credentialId !== product.credentialId

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
