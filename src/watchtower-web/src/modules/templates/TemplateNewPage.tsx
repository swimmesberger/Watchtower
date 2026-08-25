import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { ChevronLeft } from 'lucide-react'
import { api } from '@/lib/api'
import type { TemplateEnvVarInput } from '@/lib/types'
import { deriveProductName } from '@/lib/source'
import { useRealms } from '@/hooks/use-realms'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
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
import { templateNewRoute } from './module'

const NO_CREDENTIAL = 'none'

export function TemplateNewPage() {
  const qc = useQueryClient()
  const navigate = useNavigate()

  const { data: credentials = [] } = useQuery({ queryKey: ['credentials'], queryFn: api.credentials.list })
  // realms.list is [RequireRole("Admin")] and this route is gated on the Tenancy module only, so a
  // non-administrator must not fetch it. Without the roster the realm select is absent and the omitted
  // realmId lands the template in the operator realm — exactly what happened before realms existed.
  const { caps } = templateNewRoute.useRouteContext()
  const { realms, systemRealmId } = useRealms({ enabled: caps.hasRole('Admin') })
  // The same Source card as /stacks/new: with a catalogue to choose from, a template should reference
  // an existing product rather than re-type a repository Watchtower already knows (ADR-0026).
  const { data: products = [] } = useQuery({
    queryKey: ['products'],
    queryFn: api.products.list,
    enabled: caps.isModuleEnabled('Products'),
  })

  const [sourceMode, setSourceMode] = useState<'new' | 'existing'>('new')
  const [productId, setProductId] = useState<number | null>(null)
  // Empty means "inherit the product default", which is what the backend stores as no override.
  const [branchOverride, setBranchOverride] = useState('')
  // Stops the derived name from overwriting one the operator typed themselves.
  const [nameTouched, setNameTouched] = useState(false)

  const [form, setForm] = useState({
    name: '',
    repositoryUrl: '',
    composeFilePath: 'docker-compose.yml',
    branch: 'main',
    credentialId: null as number | null,
    domainPattern: '{tenant}.example.com',
    targetServiceName: 'web',
    targetPort: '3000',
    // null until the roster has loaded, at which point the operator realm is the default — the same one
    // the server picks for an omitted realmId.
    realmId: null as number | null,
  })
  const [envDraft, setEnvDraft] = useState<TemplateEnvVarInput[]>([{ key: '', value: '' }])
  const [error, setError] = useState<string | null>(null)

  const selectedProduct = products.find((p) => p.id === productId) ?? null
  // Rendered only once there is something to choose between.
  const offerProducts = products.length > 0

  const create = useMutation({
    mutationFn: () => {
      const baseEnvVars = envDraft.filter((v) => v.key.trim() !== '')
      const usingProduct = sourceMode === 'existing' && selectedProduct != null
      return api.templates.create({
        name: form.name,
        // Either a product id or the repository fields — never both.
        productId: usingProduct ? selectedProduct.id : null,
        repositoryUrl: usingProduct ? '' : form.repositoryUrl,
        composeFilePath: usingProduct ? '' : form.composeFilePath,
        // An unchanged branch clears the override server-side, so the effective value is safe to send.
        branch: usingProduct ? branchOverride || selectedProduct.defaultBranch : form.branch,
        // The product owns the clone credential; a second one here would only be refused.
        credentialId: usingProduct ? null : form.credentialId,
        domainPattern: form.domainPattern,
        targetServiceName: form.targetServiceName,
        targetPort: Number(form.targetPort),
        baseEnvVars: baseEnvVars.length > 0 ? baseEnvVars : null,
        realmId: form.realmId,
      })
    },
    onSuccess: (t) => {
      qc.invalidateQueries({ queryKey: ['templates'] })
      // The realm roster carries a templateCount, and the Realms screen's delete guard reads it.
      qc.invalidateQueries({ queryKey: ['realms'] })
      // The template either joined a product or created one; both change the catalogue's counts.
      qc.invalidateQueries({ queryKey: ['products'] })
      qc.invalidateQueries({ queryKey: ['product', t.productId] })
      toast.success(`Template ${t.name} created.`)
      navigate({ to: '/templates/$id', params: { id: String(t.id) } })
    },
    onError: (err: Error) => {
      setError(err.message)
      toast.error(err.message)
    },
  })

  function set<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  /** Fills the template name from whatever the chosen source is called, unless one was typed. */
  function suggestName(from: string) {
    if (nameTouched || form.name) return
    if (from) set('name', from)
  }

  function pickProduct(value: string) {
    const id = Number(value)
    setProductId(id)
    setBranchOverride('')
    const product = products.find((p) => p.id === id)
    if (product) suggestName(product.name)
  }

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    if (!form.domainPattern.includes('{tenant}')) {
      setError('Domain pattern must contain {tenant}.')
      return
    }
    if (sourceMode === 'existing' && selectedProduct == null) {
      setError('Select a product, or switch to a new git repository.')
      return
    }
    create.mutate()
  }

  return (
    <div className="mx-auto max-w-[720px]">
      <Link
        to="/templates"
        className="inline-flex items-center gap-1 text-[13px] text-text-2 transition-colors hover:text-text"
      >
        <ChevronLeft className="size-4" />
        Templates
      </Link>

      <h1 className="mt-3 text-2xl font-semibold tracking-tight text-text">New template</h1>
      <p className="mt-1 text-[13px] text-text-2">
        A template is deployed once per tenant, each on its own subdomain, fully isolated.
      </p>

      <form onSubmit={submit} className="mt-6 space-y-6">
        <Card>
          <CardContent>
            <SectionHeader title="Source" />
            <div className="space-y-4">
              {offerProducts && (
                <div className="grid gap-2 sm:grid-cols-2">
                  <SourceModeOption
                    label="New git repository"
                    checked={sourceMode === 'new'}
                    onSelect={() => setSourceMode('new')}
                  />
                  <SourceModeOption
                    label="Existing product"
                    checked={sourceMode === 'existing'}
                    onSelect={() => setSourceMode('existing')}
                  />
                </div>
              )}

              <Field label="Template name" required>
                {({ id, describedBy }) => (
                  <Input id={id} aria-describedby={describedBy} value={form.name}
                    onChange={(e) => { setNameTouched(true); set('name', e.target.value) }}
                    required placeholder="saas-app" autoComplete="off" />
                )}
              </Field>

              {sourceMode === 'existing' ? (
                <>
                  <Field label="Product" required>
                    {({ id, describedBy }) => (
                      <Select
                        value={productId != null ? String(productId) : ''}
                        onValueChange={pickProduct}
                      >
                        <SelectTrigger id={id} aria-describedby={describedBy}>
                          <SelectValue placeholder="Select a product" />
                        </SelectTrigger>
                        <SelectContent>
                          {products.map((p) => (
                            <SelectItem key={p.id} value={String(p.id)}>
                              {p.name}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    )}
                  </Field>

                  {selectedProduct && (
                    <>
                      <p className="truncate font-mono text-[13px] text-text-2">
                        {selectedProduct.repositoryUrl} · {selectedProduct.composeFilePath}
                      </p>
                      <Field
                        label="Branch"
                        hint={`Leave empty to follow the product (${selectedProduct.defaultBranch}).`}
                      >
                        {({ id, describedBy }) => (
                          <Input id={id} aria-describedby={describedBy} mono value={branchOverride}
                            onChange={(e) => setBranchOverride(e.target.value)}
                            placeholder={selectedProduct.defaultBranch} autoComplete="off"
                            spellCheck={false} />
                        )}
                      </Field>
                    </>
                  )}
                </>
              ) : (
                <>
                  <Field label="Repository URL" required>
                    {({ id, describedBy }) => (
                      <Input id={id} aria-describedby={describedBy} mono value={form.repositoryUrl}
                        onChange={(e) => set('repositoryUrl', e.target.value)}
                        onBlur={() => suggestName(deriveProductName(form.repositoryUrl))} required
                        placeholder="https://github.com/owner/repo" autoComplete="off" spellCheck={false} />
                    )}
                  </Field>
                  <div className="grid gap-4 md:grid-cols-2">
                    <Field label="Compose file path">
                      {({ id, describedBy }) => (
                        <Input id={id} aria-describedby={describedBy} mono value={form.composeFilePath}
                          onChange={(e) => set('composeFilePath', e.target.value)}
                          placeholder="docker-compose.yml" autoComplete="off" spellCheck={false} />
                      )}
                    </Field>
                    <Field label="Branch">
                      {({ id, describedBy }) => (
                        <Input id={id} aria-describedby={describedBy} mono value={form.branch}
                          onChange={(e) => set('branch', e.target.value)} placeholder="main"
                          autoComplete="off" spellCheck={false} />
                      )}
                    </Field>
                  </div>
                  <Field label="Credential" hint="Only needed for private repositories">
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
                  {/* No product footer sentence here on purpose: each new noun is taught in exactly
                      one primary place, and "product" is taught on stack-create (design.md
                      §Explanation strategy). */}
                </>
              )}
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent>
            <SectionHeader title="Routing" description="Each tenant gets a subdomain routed to one service." />
            <div className="space-y-4">
              <Field label="Domain pattern" required hint="Use {tenant} where the tenant slug goes">
                {({ id, describedBy }) => (
                  <Input id={id} aria-describedby={describedBy} mono value={form.domainPattern}
                    onChange={(e) => set('domainPattern', e.target.value)} required
                    placeholder="{tenant}.example.com" autoComplete="off" spellCheck={false} />
                )}
              </Field>
              <div className="grid grid-cols-2 gap-4">
                <Field label="Target service" required>
                  {({ id, describedBy }) => (
                    <Input id={id} aria-describedby={describedBy} mono value={form.targetServiceName}
                      onChange={(e) => set('targetServiceName', e.target.value)} required
                      placeholder="web" autoComplete="off" spellCheck={false} />
                  )}
                </Field>
                <Field label="Target port" required>
                  {({ id, describedBy }) => (
                    <Input id={id} aria-describedby={describedBy} mono type="number" min={1} max={65535}
                      value={form.targetPort} onChange={(e) => set('targetPort', e.target.value)} required />
                  )}
                </Field>
              </div>
              {/* Only worth showing once there is more than one population to choose between. Placed with
                  routing because it is the same kind of decision: which accounts every tenant of this
                  template signs in with, and which login host they are sent to. */}
              {realms.length > 1 && (
                <Field
                  label="Realm"
                  hint="The population every tenant of this template belongs to. Moving it later is refused once the template has tenants."
                >
                  {({ id, describedBy }) => (
                    <Select
                      value={String(form.realmId ?? systemRealmId)}
                      onValueChange={(v) => set('realmId', Number(v))}
                    >
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
              )}
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent>
            <SectionHeader title="Base environment variables" description="Shared defaults; each tenant can override." />
            <EnvVarEditor value={envDraft} onChange={setEnvDraft} />
          </CardContent>
        </Card>

        {error && <Banner tone="danger" title="Could not create template">{error}</Banner>}

        <div className="flex justify-end gap-3">
          <Button asChild variant="secondary">
            <Link to="/templates">Cancel</Link>
          </Button>
          <Button type="submit" loading={create.isPending}>
            Create template
          </Button>
        </div>
      </form>
    </div>
  )
}

/**
 * One of the two source choices. A native radio in the app's existing bordered-label idiom (the
 * backup restore picker uses the same shape) rather than a new segmented-control primitive.
 */
function SourceModeOption({
  label,
  checked,
  onSelect,
}: {
  label: string
  checked: boolean
  onSelect: () => void
}) {
  return (
    <label className="flex cursor-pointer items-center gap-3 rounded-md border border-border px-3 py-2 hover:bg-surface-2">
      <input
        type="radio"
        name="template-source-mode"
        checked={checked}
        onChange={onSelect}
        className="size-4 shrink-0 accent-[var(--brand)]"
      />
      <span className="text-sm text-text">{label}</span>
    </label>
  )
}
