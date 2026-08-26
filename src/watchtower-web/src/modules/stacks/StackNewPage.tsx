import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { ChevronLeft } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateStackRequest, Product, StackEnvVarInput } from '@/lib/types'
import { deriveProductName } from '@/lib/source'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { SectionHeader } from '@/components/ui/section-header'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { SecretField } from '@/components/ui/secret-field'
import { Banner } from '@/components/ui/banner'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { EnvVarEditor } from '@/components/env-var-editor'
import { toast } from '@/components/ui/use-toast'
import { randomUuid } from '@/lib/utils'
import { stackNewRoute } from './module'

const NO_CREDENTIAL = 'none'

/** Two random UUIDs, hyphens stripped — the same recipe used in stack settings (A12). */
function generateWebhookToken() {
  return (randomUuid() + randomUuid()).replaceAll('-', '')
}

type SourceMode = 'new' | 'existing'

/** Stable empty catalogue, so the reset effect below does not re-run on every render. */
const NO_PRODUCTS: Product[] = []

export function StackNewPage() {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const { caps } = stackNewRoute.useRouteContext()
  const { productId: preselectedProductId } = stackNewRoute.useSearch()

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  // The catalogue only exists when the backend module does; without it this page is exactly the
  // form it has always been.
  const productsEnabled = caps.isModuleEnabled('Products')
  const productsQuery = useQuery({
    queryKey: ['products'],
    queryFn: api.products.list,
    enabled: productsEnabled,
  })
  const products = productsQuery.data ?? NO_PRODUCTS
  // "Settled" covers the module being switched off (the query never runs) and a failed fetch as
  // much as a successful one — all three are answers about what the catalogue can offer.
  const catalogueSettled =
    !productsEnabled || productsQuery.isSuccess || productsQuery.isError

  const [sourceMode, setSourceMode] = useState<SourceMode>(
    preselectedProductId != null ? 'existing' : 'new',
  )
  const [productId, setProductId] = useState<number | null>(preselectedProductId ?? null)
  // Branch in existing-product mode: empty means "inherit the product default", which is what the
  // backend stores as no override at all.
  const [branchOverride, setBranchOverride] = useState('')
  // Stops the derived name from overwriting one the operator typed themselves.
  const [nameTouched, setNameTouched] = useState(false)

  const [form, setForm] = useState<Omit<CreateStackRequest, 'envVars' | 'productId'>>({
    name: '',
    repositoryUrl: '',
    composeFilePath: 'docker-compose.yml',
    branch: 'main',
    composeProjectName: '',
    credentialId: null,
    webhookToken: '',
    webhookEnabled: false,
  })
  const [envDraft, setEnvDraft] = useState<StackEnvVarInput[]>([{ key: '', value: '' }])
  const [showAdvanced, setShowAdvanced] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const selectedProduct = products.find((p) => p.id === productId) ?? null
  // Normally rendered only once there is something to choose between — with zero products the page
  // is the plain repository form it has always been (ADR-0026's implicit-product contract). The
  // second clause keeps the control on screen whenever the form *is* in existing-product mode, so a
  // ?productId= arriving before the catalogue does can never leave the mode unswitchable.
  const offerProducts = products.length > 0 || sourceMode === 'existing'

  const create = useMutation({
    mutationFn: (data: CreateStackRequest) => api.stacks.create(data),
    onSuccess: (stack) => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      // A new stack either joined a product or created one — both change the catalogue's counts.
      qc.invalidateQueries({ queryKey: ['products'] })
      qc.invalidateQueries({ queryKey: ['product', stack.productId] })
      toast.success(`Stack ${stack.name} created.`)
      navigate({ to: '/stacks/$id', params: { id: String(stack.id) } })
    },
    onError: (err: Error) => {
      setError(err.message)
      toast.error(err.message)
    },
  })

  function field<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  /** Fills the stack name from whatever the chosen source is called, unless the operator typed one. */
  function suggestName(from: string) {
    if (nameTouched || form.name) return
    if (from) field('name', from)
  }

  function pickProduct(value: string) {
    const id = Number(value)
    setProductId(id)
    setBranchOverride('')
    const product = products.find((p) => p.id === id)
    if (product) suggestName(product.name)
  }

  // Arriving from the product page with ?productId= must name the stack the same way picking the
  // product from the select does — and the catalogue only resolves after the first render.
  useEffect(() => {
    if (selectedProduct) suggestName(selectedProduct.name)
    // Keyed on the resolved product alone: suggestName is a no-op once the field has a value.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedProduct?.id])

  // A ?productId= the catalogue cannot resolve — a deleted product, or the Products module switched
  // off — must not strand the form in a mode whose picker has nothing to offer. Once the catalogue
  // has answered, fall back to the plain repository form rather than to an empty select.
  useEffect(() => {
    if (sourceMode !== 'existing' || !catalogueSettled) return
    // Keyed on the live selection, not on the search param: the param never clears, so keying on it
    // would re-fire this reset every time the operator clicked back into the mode and the radio
    // would snap away under them. Cleared to null by the reset below, a manual switch then falls
    // through to the catalogue check — and a product deleted mid-session still lands here.
    const selectionMissing = productId != null && !products.some((p) => p.id === productId)
    // An operator who switched to this mode themselves keeps it as long as there is a catalogue.
    if (!selectionMissing && products.length > 0) return
    setSourceMode('new')
    setProductId(null)
  }, [catalogueSettled, productId, products, sourceMode])

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    const envVars = envDraft.filter((v) => v.key.trim() !== '')
    const usingProduct = sourceMode === 'existing' && selectedProduct != null

    if (sourceMode === 'existing' && !usingProduct) {
      setError('Select a product, or switch to a new git repository.')
      return
    }

    create.mutate({
      name: form.name,
      // Either a product id or the repository fields — never both; the backend refuses that pair
      // rather than guessing a precedence rule.
      productId: usingProduct ? selectedProduct.id : null,
      repositoryUrl: usingProduct ? '' : form.repositoryUrl,
      composeFilePath: usingProduct ? '' : form.composeFilePath,
      // An unchanged branch clears the override server-side, so sending the effective value is safe.
      branch: usingProduct ? branchOverride || selectedProduct.defaultBranch : form.branch,
      composeProjectName: form.composeProjectName || null,
      // The product owns the clone credential; offering a second one here would be a field the
      // backend then refuses.
      credentialId: usingProduct ? null : form.credentialId,
      // The UI default design.md §"Auto-deploy precedence" mandates for a Releases-mode product: the
      // model still defaults to Off, and a new instance of a product whose CI publishes releases that
      // silently ignored them would be the wrong first impression. This form has no automation
      // selector to pre-set, so the default is applied to the request and stated in the Source card
      // above; stack Settings is where it is changed.
      autoDeployMode:
        usingProduct && selectedProduct.releaseMode === 'releases' ? 'onChange' : undefined,
      webhookEnabled: form.webhookEnabled,
      webhookToken: form.webhookEnabled ? form.webhookToken || null : null,
      ...(envVars.length > 0 ? { envVars } : {}),
    })
  }

  return (
    <div className="mx-auto max-w-[720px]">
      <Link
        to="/stacks"
        className="inline-flex items-center gap-1 text-[13px] text-text-2 transition-colors hover:text-text"
      >
        <ChevronLeft className="size-4" />
        Stacks
      </Link>

      <h1 className="mt-3 text-2xl font-semibold tracking-tight text-text">New stack</h1>
      <p className="mt-1 text-[13px] text-text-2">
        Point Watchtower at a git repository with a compose file to deploy.
      </p>

      <form onSubmit={submit} className="mt-6 space-y-6">
        {/* ── Source ── */}
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

              <Field label="Stack name" required>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    name="name"
                    value={form.name}
                    onChange={(e) => {
                      setNameTouched(true)
                      field('name', e.target.value)
                    }}
                    required
                    placeholder="web-app"
                    autoComplete="off"
                  />
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
                          <SelectValue
                            placeholder={catalogueSettled ? 'Select a product' : 'Loading…'}
                          />
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
                      {/* The repository fields collapse to what the product already decided. */}
                      <p className="truncate font-mono text-[13px] text-text-2">
                        {selectedProduct.repositoryUrl} · {selectedProduct.composeFilePath}
                      </p>

                      <Field
                        label="Branch"
                        hint={`Leave empty to follow the product (${selectedProduct.defaultBranch}).`}
                      >
                        {({ id, describedBy }) => (
                          <Input
                            id={id}
                            aria-describedby={describedBy}
                            mono
                            value={branchOverride}
                            onChange={(e) => setBranchOverride(e.target.value)}
                            placeholder={selectedProduct.defaultBranch}
                            autoComplete="off"
                            spellCheck={false}
                          />
                        )}
                      </Field>

                      {/* States the default the request below carries, rather than leaving it
                          invisible. */}
                      {selectedProduct.releaseMode === 'releases' && (
                        <p className="text-[13px] text-text-2">
                          New releases deploy automatically. Change this in the stack’s settings.
                        </p>
                      )}
                    </>
                  )}
                </>
              ) : (
                <>
                  <Field label="Repository URL" required>
                    {({ id, describedBy }) => (
                      <Input
                        id={id}
                        aria-describedby={describedBy}
                        mono
                        name="repositoryUrl"
                        value={form.repositoryUrl}
                        onChange={(e) => field('repositoryUrl', e.target.value)}
                        // On blur rather than per keystroke: a name derived mid-typing flickers
                        // through every prefix of the repo path.
                        onBlur={() => suggestName(deriveProductName(form.repositoryUrl))}
                        required
                        placeholder="https://github.com/owner/repo"
                        autoComplete="off"
                        spellCheck={false}
                      />
                    )}
                  </Field>

                  <div className="grid gap-4 md:grid-cols-2">
                    <Field
                      label="Compose file path"
                      hint="Relative to the repo root, e.g. docker-compose.yml"
                    >
                      {({ id, describedBy }) => (
                        <Input
                          id={id}
                          aria-describedby={describedBy}
                          mono
                          name="composeFilePath"
                          value={form.composeFilePath}
                          onChange={(e) => field('composeFilePath', e.target.value)}
                          placeholder="docker-compose.yml"
                          autoComplete="off"
                          spellCheck={false}
                        />
                      )}
                    </Field>

                    <Field label="Branch" hint="Defaults to main">
                      {({ id, describedBy }) => (
                        <Input
                          id={id}
                          aria-describedby={describedBy}
                          mono
                          name="branch"
                          value={form.branch}
                          onChange={(e) => field('branch', e.target.value)}
                          placeholder="main"
                          autoComplete="off"
                          spellCheck={false}
                        />
                      )}
                    </Field>
                  </div>

                  <Field label="Credential" hint="Only needed for private repositories">
                    {({ id, describedBy }) => (
                      <Select
                        value={form.credentialId != null ? String(form.credentialId) : NO_CREDENTIAL}
                        onValueChange={(v) =>
                          field('credentialId', v === NO_CREDENTIAL ? null : Number(v))
                        }
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

                </>
              )}

              <Field label="Compose project name" hint="Defaults to the stack name">
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    mono
                    name="composeProjectName"
                    value={form.composeProjectName ?? ''}
                    onChange={(e) => field('composeProjectName', e.target.value)}
                    placeholder={form.name || 'web-app'}
                    autoComplete="off"
                    spellCheck={false}
                  />
                )}
              </Field>

              {/* The entire product education for the hobby persona: one quiet footer sentence, no
                  interaction, no modal (design.md §Hobby flow). Only under the new-repository
                  variant — picking an existing product already names the noun. */}
              {sourceMode === 'new' && (
                <p className="text-[13px] text-text-2">
                  Watchtower saves this repository as a{' '}
                  <strong className="font-medium">product</strong> — add CI, releases or more
                  instances later without repeating yourself.
                </p>
              )}
            </div>
          </CardContent>
        </Card>

        {/* ── Environment variables ── */}
        <Card>
          <CardContent>
            <SectionHeader title="Environment variables" />

            <EnvVarEditor value={envDraft} onChange={setEnvDraft} />

            <p className="mt-3 text-xs text-text-3">
              Written to an <code className="font-mono text-text-2">--env-file</code> on every
              deploy and interpolated into the compose file as{' '}
              <code className="font-mono text-text-2">${'{KEY}'}</code>.
            </p>
          </CardContent>
        </Card>

        {/* ── Advanced ── the two fields a first-timer cannot evaluate. */}
        <Card>
          <CardContent>
            <button
              type="button"
              className="text-[13px] text-text-2 underline-offset-2 hover:text-text hover:underline"
              onClick={() => setShowAdvanced((v) => !v)}
            >
              {showAdvanced ? 'Hide' : 'Show'} advanced
            </button>

            {showAdvanced && (
              <div className="mt-4 space-y-4">
                <div className="flex items-start justify-between gap-4">
                  <div className="min-w-0">
                    <Label htmlFor="webhook-enabled">Enable webhook</Label>
                    <p className="mt-1 text-xs text-text-3">
                      Expose a deploy webhook your CI can call after each push.
                    </p>
                  </div>
                  <Switch
                    id="webhook-enabled"
                    checked={form.webhookEnabled ?? false}
                    onCheckedChange={(on) => field('webhookEnabled', on)}
                  />
                </div>

                {form.webhookEnabled && (
                  <Field
                    label="Webhook token"
                    hint="Sent as a Bearer token by your CI. Leave blank to allow unauthenticated deploys (not recommended)."
                  >
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                      <SecretField
                        value={form.webhookToken ?? ''}
                        onChange={(v) => field('webhookToken', v)}
                        placeholder="Leave blank for unauthenticated deploys"
                        aria-label="Webhook token"
                        className="flex-1"
                      />
                      <Button
                        type="button"
                        variant="secondary"
                        size="md"
                        onClick={() => field('webhookToken', generateWebhookToken())}
                        className="shrink-0"
                      >
                        Generate
                      </Button>
                    </div>
                  </Field>
                )}
              </div>
            )}
          </CardContent>
        </Card>

        {error && (
          <Banner tone="danger" title="Could not create stack">
            {error}
          </Banner>
        )}

        {/* ── Desktop footer ── */}
        <div className="hidden justify-end gap-3 md:flex">
          <Button asChild variant="secondary">
            <Link to="/stacks">Cancel</Link>
          </Button>
          <Button type="submit" loading={create.isPending}>
            Create stack
          </Button>
        </div>

        {/* ── Mobile sticky primary action, above the bottom tab bar (§6) ── */}
        <div className="fixed inset-x-0 bottom-bottombar z-20 border-t border-border bg-surface/95 p-4 backdrop-blur md:hidden">
          <Button type="submit" loading={create.isPending} className="w-full">
            Create stack
          </Button>
        </div>
      </form>

      {/* Spacer so the mobile sticky bar never overlaps the last card. */}
      <div className="h-20 md:hidden" aria-hidden />
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
        name="stack-source-mode"
        checked={checked}
        onChange={onSelect}
        className="size-4 shrink-0 accent-[var(--brand)]"
      />
      <span className="text-sm text-text">{label}</span>
    </label>
  )
}
