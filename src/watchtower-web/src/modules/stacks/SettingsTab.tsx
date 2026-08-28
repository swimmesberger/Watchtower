import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useRouteContext } from '@tanstack/react-router'
import { api } from '@/lib/api'
import { apiBase } from '@/lib/config'
import { usesReleases } from '@/lib/release'
import type { AutoDeployMode, Stack, StackEnvVarInput, UpdateStackRequest } from '@/lib/types'
import { EnvVarEditor } from '@/components/env-var-editor'
import {
  DeviceMappingEditor,
  GpuServiceEditor,
  blankDeviceRow,
  isDeviceRowBlank,
  type DeviceMappingRow,
} from '@/components/device-mapping-editor'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { CopyButton } from '@/components/ui/copy-button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { SecretField } from '@/components/ui/secret-field'
import { SectionHeader } from '@/components/ui/section-header'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'

function webhookUrl(stackId: number): string {
  const base = apiBase || (typeof window !== 'undefined' ? window.location.origin : '')
  return `${base}/api/webhooks/stacks/${stackId}/deploy`
}

export function SettingsTab({ stack }: { stack: Stack }) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const stackId = stack.id

  const envQuery = useQuery({
    queryKey: ['stacks', stackId, 'env'],
    queryFn: () => api.stacks.getEnv(stackId),
  })

  const devicesQuery = useQuery({
    queryKey: ['stacks', stackId, 'devices'],
    queryFn: () => api.stacks.getDevices(stackId),
  })

  // Host-wide, not per stack — what "map host GPU(s)" would resolve to on this Docker host.
  const hostGpusQuery = useQuery({
    queryKey: ['host', 'gpus'],
    queryFn: () => api.stacks.hostGpus(),
    staleTime: 60_000,
  })

  // Only to decide whether the product is linkable; the branch hint below is derived from the stack
  // DTO alone, because it is the only source that cannot disagree with what the backend compares.
  const { caps } = useRouteContext({ from: '__root__' })
  const productsEnabled = caps.isModuleEnabled('Products')

  // The three product-owned fields are deliberately absent: stacks.update refuses a *changed* one,
  // and a value seeded at mount goes stale the moment someone edits the product elsewhere — which
  // would then fail every save here with a refusal naming a control this form no longer shows.
  const [form, setForm] = useState<
    Omit<UpdateStackRequest, 'envVars' | 'repositoryUrl' | 'composeFilePath' | 'credentialId'>
  >({
    name: stack.name,
    branch: stack.branch,
    composeProjectName: stack.composeProjectName,
    webhookToken: stack.webhookToken ?? '',
    webhookEnabled: stack.webhookEnabled,
    autoDeployMode: stack.autoDeployMode,
    autoDeployTime: stack.autoDeployTime ?? '02:00',
  })

  // Draft is null until the user edits; displayed rows fall back to the loaded server vars.
  // Seeding useState from the query raced the fetch — on a cold cache the editor rendered
  // empty and stayed empty even after the vars arrived.
  const [envDraft, setEnvDraft] = useState<StackEnvVarInput[] | null>(null)
  const envRows: StackEnvVarInput[] = envDraft ?? [
    ...(envQuery.data ?? []).map((v) => ({ key: v.key, value: v.value })),
    { key: '', value: '' },
  ]

  // Same draft-or-fallback shape as the env editor, for the same cold-cache reason.
  const [deviceDraft, setDeviceDraft] = useState<DeviceMappingRow[] | null>(null)
  const deviceRows: DeviceMappingRow[] = deviceDraft ?? [
    ...(devicesQuery.data?.devices ?? []).map((d) => ({
      service: d.service,
      hostPath: d.hostPath,
      // Stored resolved (the backend defaults it to the host path); shown blank when they agree so
      // the common case reads as "same as host" rather than as a deliberate second path.
      containerPath: d.containerPath === d.hostPath ? '' : d.containerPath,
      permissions: d.permissions ?? '',
    })),
    blankDeviceRow,
  ]

  const [gpuDraft, setGpuDraft] = useState<string[] | null>(null)
  const gpuRows: string[] = gpuDraft ?? [...(devicesQuery.data?.gpuServices ?? []), '']

  const [confirmDelete, setConfirmDelete] = useState(false)

  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) =>
    setForm((prev) => ({ ...prev, [key]: value }))

  const update = useMutation({
    mutationFn: async (data: UpdateStackRequest) => {
      const updated = await api.stacks.update(stackId, data)
      // Devices ride the same Save, but only when the user actually edited them — sending the
      // fallback rows while the query is unresolved would silently wipe the stored mappings. One
      // RPC replaces both lists, so the unedited one is re-sent from the loaded data (present
      // whenever a draft exists: the editors only render after the query resolved).
      if ((deviceDraft || gpuDraft) && devicesQuery.data) {
        const devices = deviceDraft
          ? deviceDraft.filter((r) => !isDeviceRowBlank(r)).map((r) => ({
              service: r.service.trim(),
              hostPath: r.hostPath.trim(),
              containerPath: r.containerPath.trim() || null,
              permissions: r.permissions.trim() || null,
            }))
          : devicesQuery.data.devices
        const gpuServices = gpuDraft
          ? gpuDraft.map((s) => s.trim()).filter((s) => s !== '')
          : devicesQuery.data.gpuServices
        await api.stacks.setDevices(stackId, devices, gpuServices)
      }
      return updated
    },
    onSuccess: (updated) => {
      qc.setQueryData(['stacks', stackId], updated)
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'env'] })
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'devices'] })
      qc.invalidateQueries({ queryKey: ['stacks'] })
      toast.success('Settings saved.')
    },
    onError: (err: Error) => toast.error('Save failed', err.message),
  })

  const deleteStack = useMutation({
    mutationFn: () => api.stacks.delete(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      toast.success(`Deleted ${stack.name}.`)
      navigate({ to: '/stacks' })
    },
    onError: (err: Error) => toast.error('Delete failed', err.message),
    onSettled: () => setConfirmDelete(false),
  })

  function handleSave(e: React.FormEvent) {
    e.preventDefault()
    update.mutate({
      ...form,
      // Empty is "not supplied" to the handler's Changed() check, and a null credential is only
      // refused when it names a *different* one — so all three pass without this form owning them.
      repositoryUrl: '',
      composeFilePath: '',
      credentialId: null,
      composeProjectName: form.composeProjectName || null,
      webhookToken: form.webhookToken || null,
      autoDeployTime: form.autoDeployMode === 'scheduled' ? form.autoDeployTime : null,
      // Only replace env vars when the user actually edited them — sending the fallback
      // rows while the env query is unresolved would silently wipe the stored vars.
      envVars: envDraft?.filter((v) => v.key.trim() !== ''),
    })
  }

  // Derived from the stack DTO alone. With no override, the effective branch *is* the branch this
  // stack inherits — the exact value the handler compares against — so it can be named. With one
  // set, the inherited value is not on the DTO (for a tenant it is the template's override, not the
  // product default), so the hint states the state instead of guessing a number.
  const branchHint = stack.branchOverride
    ? 'Pinned; clear to inherit.'
    : `Overrides the inherited branch (${stack.branch}) for this stack.`

  // The one binary this form reads: it relabels the automation section rather than adding a second
  // one, and a pin parks the whole section (design.md §Stack detail).
  const releaseMode = usesReleases(stack)
  const rolloutPaused = releaseMode && stack.pinnedRelease != null

  const url = webhookUrl(stackId)
  // Always the authenticated form: an enabled webhook without a token now refuses every call
  // (ADR-0026 retrofitted the deploy webhook onto the constant-time bearer check), so a copyable
  // command without the header would only produce a 401.
  const curlHint = `curl -X POST -H "Authorization: Bearer <token>" ${url}`

  return (
    <form onSubmit={handleSave} className="max-w-2xl space-y-8">
      {/* Configuration */}
      <section>
        <SectionHeader
          title="Configuration"
          description="Where the compose project lives and how it’s deployed."
        />
        <Card>
          <CardContent className="space-y-4">
            <Field label="Stack name" required>
              {({ id }) => (
                <Input
                  id={id}
                  mono
                  value={form.name}
                  onChange={(e) => set('name', e.target.value)}
                  required
                />
              )}
            </Field>

            {/* Demoted, not deleted (design.md §Stack detail): the repository URL, compose file and
                credential live on the product since ADR-0026 and editing them here now errors
                server-side, so the control is replaced by a read-only row in the same position that
                points at where the value moved. */}
            <div>
              <p className="text-[13px] text-text-2">
                From product{' '}
                {productsEnabled ? (
                  <Link
                    to="/products/$id"
                    params={{ id: String(stack.productId) }}
                    className="font-medium text-text hover:text-brand"
                  >
                    {stack.productName}
                  </Link>
                ) : (
                  // The catalogue page is gated on the module; a link into a route that redirects
                  // straight back out is worse than plain text.
                  <span className="font-medium text-text">{stack.productName}</span>
                )}{' '}
                — <span className="font-mono">{stack.repositoryUrl}</span> ·{' '}
                <span className="font-mono">{stack.composeFilePath}</span>
              </p>
              {productsEnabled && (
                <Link
                  to="/products/$id"
                  params={{ id: String(stack.productId) }}
                  className="mt-1 inline-block text-[13px] text-brand hover:underline"
                >
                  Edit product
                </Link>
              )}
            </div>

            <Field label="Branch" hint={branchHint}>
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  mono
                  value={form.branch}
                  onChange={(e) => set('branch', e.target.value)}
                  placeholder="main"
                />
              )}
            </Field>

            <Field label="Compose project name" hint="Defaults to the stack name">
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  aria-describedby={describedBy}
                  mono
                  value={form.composeProjectName ?? ''}
                  onChange={(e) => set('composeProjectName', e.target.value)}
                />
              )}
            </Field>
          </CardContent>
        </Card>
      </section>

      {/* Authentication */}
      <section>
        <SectionHeader
          title="Authentication"
          description="Protects the deploy webhook your CI calls. The clone credential lives on the product."
        />
        <Card>
          <CardContent className="space-y-4">
            <Field label="Webhook">
              <label className="flex items-center gap-3">
                <Switch
                  checked={form.webhookEnabled ?? false}
                  onCheckedChange={(v) => set('webhookEnabled', v)}
                />
                <span className="text-sm text-text">Enable webhook endpoint</span>
              </label>
            </Field>

            {form.webhookEnabled && (
              <>
                {!form.webhookToken && (
                  <Banner tone="warn" title="No token set">
                    Every call to this webhook is refused until you set one. Blank no longer means
                    “anyone may deploy this stack”.
                  </Banner>
                )}

                <Field
                  label="Webhook token"
                  hint="Sent as a Bearer token by your CI. Required — an enabled webhook without one refuses every call."
                >
                  <SecretField
                    value={form.webhookToken ?? ''}
                    onChange={(v) => set('webhookToken', v)}
                    aria-label="Webhook token"
                  />
                </Field>

                <Field label="Webhook URL">
                  <div className="flex items-center gap-2">
                    <Input mono readOnly value={url} aria-label="Webhook URL" />
                    <CopyButton value={url} />
                  </div>
                </Field>

                <p className="font-mono text-[12px] text-text-3">{curlHint}</p>
              </>
            )}
          </CardContent>
        </Card>
      </section>

      {/* Automatic deployment — the same three AutoDeployMode values in both modes; only the
          mechanism they name changes (design.md §"Auto-deploy precedence": one automation field,
          reinterpreted, never a second one). */}
      <section>
        <SectionHeader
          title={releaseMode ? 'Automatic rollout' : 'Automatic deployment'}
          description={
            releaseMode
              ? 'How this deployment picks up the releases your CI publishes.'
              : 'Redeploy without an inbound webhook: Watchtower polls the registry for newer images and the git branch for new commits.'
          }
        />
        <Card>
          <CardContent className="space-y-4">
            <Field label="Mode">
              <Select
                value={form.autoDeployMode ?? 'off'}
                onValueChange={(v) => set('autoDeployMode', v as AutoDeployMode)}
                disabled={rolloutPaused}
              >
                <SelectTrigger disabled={rolloutPaused}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="off">{releaseMode ? 'Off' : 'Disabled'}</SelectItem>
                  <SelectItem value="onChange">
                    {releaseMode ? 'When a new release is published' : 'When an update is detected'}
                  </SelectItem>
                  <SelectItem value="scheduled">Daily at a fixed time</SelectItem>
                </SelectContent>
              </Select>
            </Field>

            {/* Disabled with the reason inline, never hidden: a pin is the opt-out from automation
                (design.md §"Auto-deploy precedence", rule 2), and removing the control would leave
                the operator guessing why their setting stopped mattering. */}
            {rolloutPaused && (
              <p className="text-[13px] text-text-2">
                Automatic rollout is paused while this deployment is pinned to{' '}
                {stack.pinnedRelease!.version}.
              </p>
            )}

            {/* Both mode hints describe automation that is not running while a pin is set, so the
                paused reason above is the only sentence under the control. */}
            {!rolloutPaused &&
              form.autoDeployMode === 'onChange' &&
              (releaseMode ? (
                <p className="text-[13px] text-text-2">
                  Deploys within a minute of your CI reporting a new release.
                </p>
              ) : (
                <p className="text-[13px] text-text-2">
                  Polls on the stack update-check interval (Settings → Automation) and redeploys as
                  soon as a newer image or a new commit on{' '}
                  <span className="font-mono">{form.branch || 'main'}</span> is found.
                </p>
              ))}

            {/* The field itself stays — never delete a control someone has set — but its hint would
                describe a schedule the pin has suspended. */}
            {form.autoDeployMode === 'scheduled' && (
              <Field
                label="Deploy time"
                hint={
                  rolloutPaused
                    ? undefined
                    : releaseMode
                      ? 'Server-local time. Deploys the newest release once per day at this time.'
                      : 'Server-local time. Checks once per day at this time and redeploys only if something new is available.'
                }
              >
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    type="time"
                    className="max-w-40"
                    value={form.autoDeployTime ?? ''}
                    onChange={(e) => set('autoDeployTime', e.target.value)}
                    disabled={rolloutPaused}
                    required
                  />
                )}
              </Field>
            )}
          </CardContent>
        </Card>
      </section>

      {/* Environment variables */}
      <section>
        <SectionHeader
          title="Environment variables"
          description="Injected via --env-file on every deploy. Reference them as ${KEY} in your compose file."
        />
        {envQuery.isPending && (
          <p className="rounded-md border border-border px-3 py-2.5 text-sm text-text-3">
            Loading environment variables…
          </p>
        )}
        {envQuery.isError && (
          // No editor on error: editing on top of unseen vars would replace them on save.
          <Banner tone="warn" title="Couldn’t load environment variables">
            {envQuery.error.message} — saving will leave the stored variables unchanged.
          </Banner>
        )}
        {envQuery.isSuccess && <EnvVarEditor value={envRows} onChange={setEnvDraft} />}
      </section>

      {/* Device mappings (ADR-0030) */}
      <section>
        <SectionHeader
          title="Device mappings"
          description="Host devices mapped into this stack's containers on deploy — e.g. /dev/dri/renderD128 for GPU transcoding. Host-specific by design, so they live here instead of the compose file."
        />
        {devicesQuery.isPending && (
          <p className="rounded-md border border-border px-3 py-2.5 text-sm text-text-3">
            Loading device mappings…
          </p>
        )}
        {devicesQuery.isError && (
          // No editor on error, for the env editor's reason: editing on top of unseen mappings
          // would replace them on save.
          <Banner tone="warn" title="Couldn’t load device mappings">
            {devicesQuery.error.message} — saving will leave the stored mappings unchanged.
          </Banner>
        )}
        {devicesQuery.isSuccess && (
          <>
            {/* GPU passthrough (ADR-0031): a host-neutral intent — the deploy probes the host and
                maps whatever mappable render nodes exist, plus their owning groups. */}
            <p className="mb-1.5 text-sm font-medium text-text">GPU passthrough</p>
            <GpuServiceEditor value={gpuRows} onChange={setGpuDraft} />
            <p className="mt-2 text-[13px] text-text-2">
              {hostGpusQuery.data?.error != null ? (
                <>Couldn’t inspect this host’s GPUs: {hostGpusQuery.data.error}</>
              ) : hostGpusQuery.data ? (
                hostGpusQuery.data.gpus.length === 0 ? (
                  <>
                    No GPU render node detected on this Docker host — listed services deploy fine
                    and simply get no GPU here.
                  </>
                ) : (
                  <>
                    Detected:{' '}
                    {hostGpusQuery.data.gpus.map((g, i) => (
                      <span key={g.path}>
                        {i > 0 && ', '}
                        <span className="font-mono">{g.name}</span> — {g.vendor} ({g.driver},{' '}
                        {g.pciAddress}){g.mappable ? '' : ' — needs the NVIDIA toolkit, not mapped'}
                      </span>
                    ))}
                    . Each listed service gets every mappable GPU, and the required group is added
                    automatically.
                  </>
                )
              ) : (
                <>Checking this host for GPUs…</>
              )}
            </p>

            <p className="mb-1.5 mt-5 text-sm font-medium text-text">Specific devices</p>
            <DeviceMappingEditor value={deviceRows} onChange={setDeviceDraft} />
            <p className="mt-2 text-[13px] text-text-2">
              Access is some combination of <span className="font-mono">r</span>ead,{' '}
              <span className="font-mono">w</span>rite and <span className="font-mono">m</span>knod;
              blank means all three. A mapping for a service the compose file doesn’t define is
              skipped with a warning in the deploy log.
            </p>
          </>
        )}
      </section>

      {/* Save */}
      <div className="flex items-center gap-3">
        <Button type="submit" variant="primary" loading={update.isPending}>
          Save settings
        </Button>
      </div>

      {/* Danger zone */}
      <section>
        <SectionHeader title="Danger zone" />
        <Card className="border-danger-bd">
          <CardContent className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="min-w-0">
              <p className="text-sm font-medium text-text">Delete stack</p>
              <p className="mt-0.5 text-[13px] text-text-2">
                This permanently deletes the stack and its deployment history. Running containers
                are not affected.
              </p>
            </div>
            <Button
              type="button"
              variant="danger"
              className="shrink-0"
              onClick={() => setConfirmDelete(true)}
            >
              Delete stack
            </Button>
          </CardContent>
        </Card>
      </section>

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete ${stack.name}?`}
        description="This permanently deletes the stack and its deployment history. Running containers are not affected."
        confirmLabel="Delete stack"
        tone="danger"
        requireText={stack.name}
        loading={deleteStack.isPending}
        onConfirm={() => deleteStack.mutate()}
      />
    </form>
  )
}
