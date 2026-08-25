import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { ChevronLeft } from 'lucide-react'
import { api } from '@/lib/api'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
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
import { deriveProductName } from '@/lib/source'

const NO_CREDENTIAL = 'none'

export function ProductNewPage() {
  const qc = useQueryClient()
  const navigate = useNavigate()

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const [form, setForm] = useState({
    name: '',
    repositoryUrl: '',
    defaultBranch: 'main',
    composeFilePath: 'docker-compose.yml',
    credentialId: null as number | null,
  })
  // Stops the derived name from overwriting one the operator typed themselves.
  const [nameTouched, setNameTouched] = useState(false)
  const [error, setError] = useState<string | null>(null)

  function set<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  const create = useMutation({
    mutationFn: () =>
      api.products.create({
        name: form.name,
        repositoryUrl: form.repositoryUrl,
        composeFilePath: form.composeFilePath,
        defaultBranch: form.defaultBranch,
        credentialId: form.credentialId,
      }),
    onSuccess: (product) => {
      qc.invalidateQueries({ queryKey: ['products'] })
      toast.success(`Product ${product.name} created.`)
      navigate({ to: '/products/$id', params: { id: String(product.id) } })
    },
    onError: (err: Error) => {
      setError(err.message)
      toast.error(err.message)
    },
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    create.mutate()
  }

  return (
    <div className="mx-auto max-w-[720px]">
      <Link
        to="/products"
        className="inline-flex items-center gap-1 text-[13px] text-text-2 transition-colors hover:text-text"
      >
        <ChevronLeft className="size-4" />
        Products
      </Link>

      <h1 className="mt-3 text-2xl font-semibold tracking-tight text-text">New product</h1>
      {/* Stage 1 has no releases, so the sentence describes only what exists. Restore the design
          doc's wording ("its compose file, its builds, and the releases your CI publishes") when
          stage 3 ships the Releases tab. */}
      <p className="mt-1 text-[13px] text-text-2">
        A product is a git repository Watchtower can deploy — its compose file and the stacks that
        run it.
      </p>

      <form onSubmit={submit} className="mt-6 space-y-6">
        <Card>
          <CardContent>
            <SectionHeader title="Source" />
            <div className="space-y-4">
              <Field label="Product name" required>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    value={form.name}
                    onChange={(e) => {
                      setNameTouched(true)
                      set('name', e.target.value)
                    }}
                    required
                    placeholder="acme-web"
                    autoComplete="off"
                  />
                )}
              </Field>

              <Field label="Repository URL" required>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    mono
                    value={form.repositoryUrl}
                    onChange={(e) => set('repositoryUrl', e.target.value)}
                    // Filled on blur rather than per keystroke: a name derived mid-typing flickers
                    // through every prefix of the repo path.
                    onBlur={() => {
                      if (nameTouched || form.name) return
                      const derived = deriveProductName(form.repositoryUrl)
                      if (derived) set('name', derived)
                    }}
                    required
                    placeholder="https://github.com/owner/repo"
                    autoComplete="off"
                    spellCheck={false}
                  />
                )}
              </Field>

              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Branch" hint="Deployed unless a stack overrides it">
                  {({ id, describedBy }) => (
                    <Input
                      id={id}
                      aria-describedby={describedBy}
                      mono
                      value={form.defaultBranch}
                      onChange={(e) => set('defaultBranch', e.target.value)}
                      placeholder="main"
                      autoComplete="off"
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
            </div>
          </CardContent>
        </Card>

        {error && (
          <Banner tone="danger" title="Could not create product">
            {error}
          </Banner>
        )}

        <div className="flex justify-end gap-3">
          <Button asChild variant="secondary">
            <Link to="/products">Cancel</Link>
          </Button>
          <Button type="submit" loading={create.isPending}>
            Create product
          </Button>
        </div>
      </form>
    </div>
  )
}
