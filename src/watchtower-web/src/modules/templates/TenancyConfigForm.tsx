import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRouteContext } from '@tanstack/react-router'
import { api } from '@/lib/api'
import type { Product, StackTemplate, TemplateEnvVarInput } from '@/lib/types'
import { useRealms } from '@/hooks/use-realms'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { EnvVarEditor } from '@/components/env-var-editor'
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

// ── The tenancy config form (ADR-0026 stage 8b) ─────────────────────────────────
//
// design.md §"SaaS flow" step 4: "today's template form minus the entire Source card". The source is
// the *product's* — this form is opened from inside one, so there is nothing to ask and nothing to get
// wrong. It is both halves of the fold: the "Set up tenancy" create form when the product has no
// tenancy yet, and what the summary card's [Edit] expands into afterwards.
//
// The `{tenant}` placeholder is taught by the live preview under the input, never by prose
// (design.md §"Explanation strategy" — a live preview beats every explanatory sentence).

/** The two slugs the preview substitutes. Two, not one: one reads as a literal, two read as a pattern. */
const PREVIEW_SLUGS = ['acme', 'globex'] as const

/** What the pattern resolves to for a couple of tenants, or null while it cannot resolve to anything. */
function previewDomains(pattern: string): string[] | null {
  const trimmed = pattern.trim()
  if (!trimmed.includes('{tenant}')) return null
  return PREVIEW_SLUGS.map((slug) => trimmed.replace(/\{tenant\}/g, slug))
}

interface ConfigForm {
  name: string
  domainPattern: string
  targetServiceName: string
  targetPort: string
  /** Empty means "follow the product's default branch", which is what the server stores as no override. */
  branchOverride: string
  realmId: number | null
}

/**
 * Create or edit a product's tenancy setup.
 *
 * One component for both because they are the same seven fields; the differences are exactly two and
 * both are properties of the *template*, not of the form: an existing setup cannot change the realm once
 * it has instances (the server refuses it, so the control is read-only rather than absent), and a create
 * has no id to post to.
 */
export function TenancyConfigForm({
  product,
  template,
  baseEnvVars,
  onDone,
  onCancel,
}: {
  product: Product
  /** The setup being edited, or undefined to create the product's first (or another) one. */
  template?: StackTemplate
  /** The template's stored base env vars — ignored when creating. */
  baseEnvVars?: readonly { key: string; value: string }[]
  onDone: (template: StackTemplate) => void
  onCancel: () => void
}) {
  const qc = useQueryClient()
  const { caps } = useRouteContext({ from: '__root__' })
  // realms.list is [RequireRole("Admin")] and this form renders on a route gated on the module only, so
  // a non-administrator must not fetch it. Without the roster the realm control is simply absent and an
  // omitted realmId lands the setup in the operator realm — exactly what happened before realms existed.
  const { realms, systemRealmId, nameOf } = useRealms({ enabled: caps.hasRole('Admin') })

  const [form, setForm] = useState<ConfigForm>(() => ({
    // `-tenants` rather than the bare product name: template names are unique across the install, and
    // the obvious clash is a product and its own tenancy setup wanting the same one.
    name: template?.name ?? `${product.name}-tenants`,
    domainPattern: template?.domainPattern ?? '{tenant}.example.com',
    targetServiceName: template?.targetServiceName ?? 'web',
    targetPort: String(template?.targetPort ?? 3000),
    branchOverride: template?.branchOverride ?? '',
    realmId: template?.realmId ?? null,
  }))
  const [envDraft, setEnvDraft] = useState<TemplateEnvVarInput[]>(() =>
    baseEnvVars && baseEnvVars.length > 0
      ? baseEnvVars.map((v) => ({ key: v.key, value: v.value }))
      : [{ key: '', value: '' }],
  )
  const [error, setError] = useState<string | null>(null)

  const set = <K extends keyof ConfigForm>(key: K, value: ConfigForm[K]) =>
    setForm((previous) => ({ ...previous, [key]: value }))

  // The realm is fixed once the setup has instances — the server refuses the move, so showing an
  // editable control that cannot be saved would be a trap. Read-only with the reason beats hidden.
  const realmLocked = template != null && template.instanceCount > 0
  const effectiveRealmId = form.realmId ?? template?.realmId ?? systemRealmId
  /** Only worth a control once there is more than one population to choose between. */
  const realmSelectable = realms.length > 1

  const save = useMutation({
    mutationFn: () => {
      const envVars = envDraft.filter((v) => v.key.trim() !== '')
      const payload = {
        name: form.name.trim(),
        productId: product.id,
        // The source is the product's. These three are posted empty on purpose: `templates.create`
        // resolves the product from `productId`, and `templates.update` refuses only a *changed*
        // repository field — blank is "no opinion" on both paths, which is what the fold means.
        repositoryUrl: '',
        composeFilePath: '',
        credentialId: null,
        // An unchanged branch clears the override server-side, so posting the effective value is safe.
        branch: form.branchOverride.trim() || product.defaultBranch,
        domainPattern: form.domainPattern.trim(),
        targetServiceName: form.targetServiceName.trim(),
        targetPort: Number(form.targetPort),
        // An empty list is "replace the base set with nothing", which is what clearing every row means.
        baseEnvVars: envVars,
        // Null is "leave it where it is" on update and "the operator realm" on create — the answer
        // whenever this form showed no realm control to make a decision with.
        realmId: realmLocked || !realmSelectable ? null : effectiveRealmId,
      }
      return template ? api.templates.update(template.id, payload) : api.templates.create(payload)
    },
    onSuccess: (saved) => {
      // The product's template roster, its counts and the realm roster's templateCount all moved.
      qc.invalidateQueries({ queryKey: ['product', product.id] })
      qc.invalidateQueries({ queryKey: ['products'] })
      qc.invalidateQueries({ queryKey: ['template', saved.id] })
      qc.invalidateQueries({ queryKey: ['realms'] })
      // The Backups tab reads the same templates through its own query.
      qc.invalidateQueries({ queryKey: ['backups', 'product', product.id] })
      toast.success(template ? 'Tenancy settings saved.' : `Tenancy set up for ${product.name}.`)
      onDone(saved)
    },
    onError: (err: Error) => {
      setError(err.message)
      toast.error(err.message)
    },
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    if (!form.name.trim()) {
      setError('A name is required.')
      return
    }
    // The same sentence the live preview is the positive form of, so the two agree.
    if (!form.domainPattern.includes('{tenant}')) {
      setError('The domain pattern must contain {tenant} — that is where each tenant’s slug goes.')
      return
    }
    save.mutate()
  }

  const preview = previewDomains(form.domainPattern)

  return (
    <form onSubmit={submit} className="space-y-5">
      <SectionHeader
        title={template ? 'Tenancy settings' : 'Set up tenancy'}
        description={`One isolated copy of ${product.name} per tenant, each on its own subdomain.`}
      />

      <div className="grid gap-4 md:grid-cols-2">
        <Field label="Name" required hint="What this fleet of instances is called.">
          {({ id, describedBy }) => (
            <Input
              id={id}
              aria-describedby={describedBy}
              value={form.name}
              onChange={(e) => set('name', e.target.value)}
              required
              placeholder={`${product.name}-tenants`}
              autoComplete="off"
            />
          )}
        </Field>
        <Field
          label="Branch"
          hint={`Leave empty to follow ${product.name} (${product.defaultBranch}).`}
        >
          {({ id, describedBy }) => (
            <Input
              id={id}
              aria-describedby={describedBy}
              mono
              value={form.branchOverride}
              onChange={(e) => set('branchOverride', e.target.value)}
              placeholder={product.defaultBranch}
              autoComplete="off"
              spellCheck={false}
            />
          )}
        </Field>
      </div>

      <Field label="Domain pattern" required hint="Use {tenant} where the tenant slug goes">
        {({ id, describedBy }) => (
          <Input
            id={id}
            aria-describedby={describedBy}
            mono
            value={form.domainPattern}
            onChange={(e) => set('domainPattern', e.target.value)}
            required
            placeholder="{tenant}.example.com"
            autoComplete="off"
            spellCheck={false}
          />
        )}
      </Field>
      {/* The live preview *is* the explanation of {tenant} (design.md §"Explanation strategy"). It
          reads as an example list, so it also shows what a slug will look like in a browser bar. */}
      <p className="-mt-3 text-[13px] text-text-2">
        {preview ? (
          <span className="font-mono text-text-2">{preview.join(' · ')}</span>
        ) : (
          <span className="text-warn">
            Add {'{tenant}'} somewhere — without it every tenant would want the same domain.
          </span>
        )}
      </p>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Target service" required hint="The compose service each tenant’s domain routes to.">
          {({ id, describedBy }) => (
            <Input
              id={id}
              aria-describedby={describedBy}
              mono
              value={form.targetServiceName}
              onChange={(e) => set('targetServiceName', e.target.value)}
              required
              placeholder="web"
              autoComplete="off"
              spellCheck={false}
            />
          )}
        </Field>
        <Field label="Target port" required>
          {({ id, describedBy }) => (
            <Input
              id={id}
              aria-describedby={describedBy}
              mono
              type="number"
              min={1}
              max={65535}
              value={form.targetPort}
              onChange={(e) => set('targetPort', e.target.value)}
              required
            />
          )}
        </Field>
      </div>

      {/* Same decision as before the fold: which accounts every tenant signs in with, and which login
          host they are sent to. */}
      {realmSelectable &&
        (realmLocked ? (
          <Field label="Realm" hint="Fixed while this setup has instances — remove them first to move it.">
            {({ id, describedBy }) => (
              <Input id={id} aria-describedby={describedBy} value={nameOf(effectiveRealmId)} readOnly disabled />
            )}
          </Field>
        ) : (
          <Field
            label="Realm"
            hint="The population every tenant belongs to. Fixed once this setup has instances."
          >
            {({ id, describedBy }) => (
              <Select value={String(effectiveRealmId)} onValueChange={(v) => set('realmId', Number(v))}>
                <SelectTrigger id={id} aria-describedby={describedBy}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {realms.map((r) => (
                    <SelectItem key={r.id} value={String(r.id)}>
                      {r.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </Field>
        ))}

      <div>
        <SectionHeader
          title="Base environment variables"
          description="Shared defaults; each tenant can override them when it is added."
        />
        <EnvVarEditor value={envDraft} onChange={setEnvDraft} />
      </div>

      {error && (
        <Banner tone="danger" title={template ? 'Could not save' : 'Could not set up tenancy'}>
          {error}
        </Banner>
      )}

      <div className="flex justify-end gap-3">
        <Button type="button" variant="secondary" onClick={onCancel} disabled={save.isPending}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending}>
          {template ? 'Save' : 'Set up tenancy'}
        </Button>
      </div>
    </form>
  )
}
