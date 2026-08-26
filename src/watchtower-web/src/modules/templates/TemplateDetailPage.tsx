import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link, useNavigate } from '@tanstack/react-router'
import {
  ChevronLeft,
  ExternalLink,
  PlayCircle,
  Plus,
  ShieldCheck,
  Tags,
  Trash2,
  Users,
} from 'lucide-react'
import { api } from '@/lib/api'
import type { Tenant, TemplateEnvVarInput, TemplateGrant } from '@/lib/types'
import { rosterVersion, versionRollup } from '@/lib/release'
import { timeAgo } from '@/lib/format'
import { useProductReleases } from '@/hooks/use-product-releases'
import { useRealms } from '@/hooks/use-realms'
import { SetReleaseDialog, type ReleaseTarget } from '@/components/set-release-dialog'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
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
import { Spinner } from '@/components/ui/spinner'
import { StatusBadge } from '@/components/ui/status-badge'
import { Switch } from '@/components/ui/switch'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

const routeApi = getRouteApi('/templates/$id')

/** A deploy is in flight — the backend refuses teardown (409) until it settles. */
const isDeploying = (t: Tenant) =>
  t.lastDeployStatus === 'running' || t.lastDeployStatus === 'queued'

export function TemplateDetailPage() {
  const { id } = routeApi.useParams()
  const templateId = Number(id)
  const qc = useQueryClient()
  const navigate = useNavigate()
  const { caps } = routeApi.useRouteContext()
  // UX projection only: every templates.*Management / listGrants handler carries
  // [RequireRole("Admin")], which is what actually refuses the call. Without this the grants query
  // would fail with Forbidden for a non-admin and the card would lie about there being no grants.
  // templates.removeTenant is NOT admin-gated, so the per-tenant remove action stays visible.
  const canManageGrants = caps.hasRole('Admin')
  // Whether the product named in the source line is somewhere the reader can actually land.
  const productsEnabled = caps.isModuleEnabled('Products')
  // Same gate, same reason: realms.list is [RequireRole("Admin")], so a non-administrator reading a
  // template must not fetch a roster it would only be refused. The realm line then simply isn't shown.
  const { nameOrNull } = useRealms({ enabled: canManageGrants })

  const [slug, setSlug] = useState('')
  const [showOverrides, setShowOverrides] = useState(false)
  const [overrides, setOverrides] = useState<TemplateEnvVarInput[]>([{ key: '', value: '' }])
  const [confirmDeleteTemplate, setConfirmDeleteTemplate] = useState(false)
  const [grantStackId, setGrantStackId] = useState('')
  const [grantAllowDelete, setGrantAllowDelete] = useState(false)
  const [pendingRevoke, setPendingRevoke] = useState<TemplateGrant | null>(null)
  const [pendingRemoveTenant, setPendingRemoveTenant] = useState<Tenant | null>(null)
  const [removeVolumes, setRemoveVolumes] = useState(false)
  // A single mutation observer only exposes its latest call's variables, so concurrent toggles on
  // different rows need their own pending bookkeeping to keep each row disabled until it settles.
  const [allowDeletePendingIds, setAllowDeletePendingIds] = useState<ReadonlySet<number>>(
    () => new Set(),
  )
  const [rollingOut, setRollingOut] = useState(false)

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['template', templateId],
    queryFn: () => api.templates.get(templateId),
  })

  const { data: tenants = [] } = useQuery({
    queryKey: ['tenants', templateId],
    queryFn: () => api.templates.listTenants(templateId),
    refetchInterval: (q) => ((q.state.data ?? []).some(isDeploying) ? 2000 : false),
  })

  const grantsQuery = useQuery({
    queryKey: ['template-grants', templateId],
    queryFn: () => api.templates.listGrants(templateId),
    enabled: canManageGrants,
  })
  const grants = grantsQuery.data ?? []

  const stacksQuery = useQuery({
    queryKey: ['stacks'],
    queryFn: api.stacks.list,
    enabled: canManageGrants,
  })
  const stacks = stacksQuery.data ?? []

  // The product's newest releases — the shared key, so this is the same fetch the stack pages and the
  // Releases tab make. Only asked for once the template has answered, because the product id comes
  // from it, and only in Releases mode: a Git-mode product has no version policy to show.
  const productId = data?.template.productId
  const releaseMode = data?.template.releaseMode
  const { data: releaseWindow } = useProductReleases(
    productId ?? 0,
    productId != null && releaseMode === 'releases',
  )
  const releases = releaseWindow?.releases ?? []
  const newestId = releases[0]?.id ?? null
  const usesReleases = releaseMode === 'releases'

  // The backend rejects granting a template's own tenants. Already-granted stacks are filtered out
  // too — their grant is edited in place on its row rather than re-added here.
  const tenantStackIds = new Set(tenants.map((t) => t.stackId))
  const grantedStackIds = new Set(grants.map((g) => g.stackId))
  const grantableStacks = stacks.filter(
    (s) => !tenantStackIds.has(s.id) && !grantedStackIds.has(s.id),
  )
  const grantableKey = grantableStacks.map((s) => s.id).join(',')
  const pickerReady = grantsQuery.isSuccess && stacksQuery.isSuccess
  const pickerFailed = grantsQuery.isError || stacksQuery.isError
  const noGrantableStacks = pickerReady && grantableStacks.length === 0

  // A refetch can drop the picked stack out of the list (someone else granted it, or it became a
  // tenant), so clear the selection rather than let Grant submit a choice the backend would reject.
  useEffect(() => {
    if (grantStackId && !grantableKey.split(',').includes(grantStackId)) setGrantStackId('')
  }, [grantStackId, grantableKey])

  const addTenant = useMutation({
    mutationFn: () => {
      const envOverrides = overrides.filter((v) => v.key.trim() !== '')
      return api.templates.addTenant({
        templateId,
        slug,
        envOverrides: envOverrides.length > 0 ? envOverrides : null,
      })
    },
    onSuccess: (t) => {
      toast.success(`Tenant ${t.tenantSlug} created — deploying…`)
      setSlug('')
      setOverrides([{ key: '', value: '' }])
      setShowOverrides(false)
      qc.invalidateQueries({ queryKey: ['tenants', templateId] })
      qc.invalidateQueries({ queryKey: ['template', templateId] })
    },
    onError: (err: Error) => toast.error(err.message),
  })

  const deployAll = useMutation({
    mutationFn: () => api.templates.deployAll(templateId),
    onSuccess: (count) => {
      toast.info(`Deploying ${count} tenant${count === 1 ? '' : 's'}…`)
      qc.invalidateQueries({ queryKey: ['tenants', templateId] })
    },
    onError: (err: Error) => toast.error(err.message),
  })

  const removeTenant = useMutation({
    mutationFn: (t: Tenant) => api.templates.removeTenant(templateId, t.tenantSlug, removeVolumes),
    onSuccess: (slug) => {
      toast.success(`Tenant ${slug} removed.`)
      // Teardown deletes the stack row, cascading its routes away, and drops instanceCount.
      qc.invalidateQueries({ queryKey: ['tenants', templateId] })
      qc.invalidateQueries({ queryKey: ['template', templateId] })
      qc.invalidateQueries({ queryKey: ['templates'] })
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['routes'] })
    },
    onError: (err: Error) => toast.error(err.message),
    onSettled: () => setPendingRemoveTenant(null),
  })

  const grantManagement = useMutation({
    mutationFn: () =>
      api.templates.grantManagement(templateId, Number(grantStackId), grantAllowDelete),
    onSuccess: (g) => {
      toast.success(`${g.stackName} can now manage tenants of this template.`)
      setGrantStackId('')
      setGrantAllowDelete(false)
    },
    onError: (err: Error) => toast.error(err.message),
    onSettled: () => qc.invalidateQueries({ queryKey: ['template-grants', templateId] }),
  })

  // grantManagement is an upsert that preserves CreatedAt, so flipping allowDelete on an existing
  // grant is the same call — no revoke/re-grant round trip.
  const setAllowDelete = useMutation({
    mutationFn: (v: { grant: TemplateGrant; allowDelete: boolean }) =>
      api.templates.grantManagement(templateId, v.grant.stackId, v.allowDelete),
    // Optimistically flip the row so the Switch tracks the click even on slow links; the snapshot
    // is restored on error.
    onMutate: async (v) => {
      setAllowDeletePendingIds((ids) => new Set(ids).add(v.grant.stackId))
      await qc.cancelQueries({ queryKey: ['template-grants', templateId] })
      const previous = qc.getQueryData<TemplateGrant[]>(['template-grants', templateId])
      qc.setQueryData<TemplateGrant[]>(['template-grants', templateId], (old) =>
        old?.map((g) =>
          g.stackId === v.grant.stackId ? { ...g, allowDelete: v.allowDelete } : g,
        ),
      )
      return { previous }
    },
    onSuccess: (g) =>
      toast.success(
        g.allowDelete
          ? `${g.stackName} may now delete tenants.`
          : `${g.stackName} may no longer delete tenants.`,
      ),
    onError: (err: Error, _v, ctx) => {
      if (ctx?.previous) qc.setQueryData(['template-grants', templateId], ctx.previous)
      toast.error(err.message)
    },
    onSettled: (_g, _err, v) => {
      setAllowDeletePendingIds((ids) => {
        const next = new Set(ids)
        next.delete(v.grant.stackId)
        return next
      })
      qc.invalidateQueries({ queryKey: ['template-grants', templateId] })
    },
  })

  const revokeManagement = useMutation({
    mutationFn: (g: TemplateGrant) => api.templates.revokeManagement(templateId, g.stackId),
    onSuccess: (removed) =>
      removed
        ? toast.success('Management access revoked.')
        : toast.info('Grant was already revoked.'),
    onError: (err: Error) => toast.error(err.message),
    onSettled: () => {
      setPendingRevoke(null)
      qc.invalidateQueries({ queryKey: ['template-grants', templateId] })
    },
  })

  const removeTemplate = useMutation({
    mutationFn: () => api.templates.delete(templateId),
    onSuccess: () => {
      toast.success('Template deleted.')
      qc.invalidateQueries({ queryKey: ['templates'] })
      // The realm's templateCount just dropped, and the Realms screen's delete guard reads it.
      qc.invalidateQueries({ queryKey: ['realms'] })
      navigate({ to: '/templates' })
    },
    onError: (err: Error) => toast.error(err.message),
    onSettled: () => setConfirmDeleteTemplate(false),
  })

  if (isLoading) return <div className="flex justify-center p-10"><Spinner /></div>
  if (isError || !data)
    return (
      <div className="mx-auto max-w-[900px]">
        <Banner tone="danger" title="Couldn’t load template">
          {(error as Error)?.message ?? 'Not found.'}
        </Banner>
      </div>
    )

  const { template, baseEnvVars } = data

  // The volumes opt-in is per-confirmation, so it resets every time the dialog opens.
  const openRemoveTenant = (t: Tenant) => {
    setRemoveVolumes(false)
    setPendingRemoveTenant(t)
  }

  // Shared by the table cell and the mobile card so the disabled-state reason travels with both.
  const removeTenantButton = (t: Tenant) => {
    const deploying = isDeploying(t)
    return (
      <Tooltip label={deploying ? 'Deploy in progress' : 'Remove tenant'}>
        {/* A disabled button swallows pointer events and can't take focus, so the wrapping span is
            the trigger — made focusable while deploying so keyboard users get the reason too. */}
        <span className="inline-flex" tabIndex={deploying ? 0 : undefined}>
          <Button
            size="icon-sm"
            variant="ghost"
            disabled={deploying}
            aria-label={`Remove ${t.tenantSlug}`}
            onClick={() => openRemoveTenant(t)}
            className="text-text-2 hover:text-danger"
          >
            <Trash2 />
          </Button>
        </span>
      </Tooltip>
    )
  }

  // The Version cell, shared by the table and the mobile card so the two cannot disagree about what an
  // instance runs. Reads `rosterVersion`, which is the same derivation the roll-out dialog's checklist
  // uses — one answer to "where is this instance", in lib/release.ts.
  const versionCell = (t: Tenant) => {
    const { version, pinned, behind } = rosterVersion(t, newestId)
    if (!version) return <span className="text-text-3">never deployed</span>
    return (
      <span className="inline-flex items-center gap-1.5">
        <span className="font-mono text-[13px] text-text">{version}</span>
        {pinned && (
          <Badge tone="neutral" size="sm">
            pinned
          </Badge>
        )}
        {/* Quiet, never a banner — the stack pages' rule, and doubly so for a roster of 200. */}
        {behind && (
          <Badge tone="neutral" size="sm">
            behind
          </Badge>
        )}
      </span>
    )
  }

  const columns: DataListColumn<Tenant>[] = [
    {
      key: 'slug',
      header: 'Tenant',
      cell: (t) => (
        <Link
          to="/stacks/$id"
          params={{ id: String(t.stackId) }}
          className="font-medium text-text hover:text-brand"
        >
          {t.tenantSlug}
        </Link>
      ),
    },
    // Only in Releases mode (invariant 4): a Git-mode fleet has no version policy, and a column of
    // dashes would be the "two competing update mechanisms" risk in table form.
    ...(usesReleases
      ? [{ key: 'version', header: 'Version', cell: versionCell } as DataListColumn<Tenant>]
      : []),
    {
      key: 'domain',
      header: 'Domain',
      cell: (t) =>
        t.domain ? (
          <a
            href={`https://${t.domain}`}
            target="_blank"
            rel="noreferrer"
            className="inline-flex items-center gap-1.5 font-mono text-[13px] text-text-2 hover:text-brand"
          >
            {t.domain}
            <ExternalLink className="size-3.5 text-text-3" />
          </a>
        ) : (
          <span className="text-text-3">—</span>
        ),
    },
    {
      key: 'status',
      header: 'Status',
      cell: (t) => <StatusBadge status={t.lastDeployStatus} />,
    },
    {
      key: 'deployed',
      header: 'Last deployed',
      cell: (t) =>
        t.lastDeployedAt ? (
          <span className="tnum text-[13px] text-text-2">{timeAgo(t.lastDeployedAt)}</span>
        ) : (
          <span className="text-text-3">never</span>
        ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      className: 'w-px',
      cell: (t) => removeTenantButton(t),
    },
  ]

  return (
    <div className="mx-auto max-w-[1000px] space-y-6">
      <Link
        to="/templates"
        className="inline-flex items-center gap-1 text-[13px] text-text-2 transition-colors hover:text-text"
      >
        <ChevronLeft className="size-4" /> Templates
      </Link>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="inline-flex items-center gap-2 text-[24px] font-semibold tracking-[-0.02em]">
          {template.name}
          <Badge tone={template.instanceCount > 0 ? 'brand' : 'neutral'}>
            <Users className="mr-1 size-3" /> {template.instanceCount}
          </Badge>
        </h1>
        <div className="flex items-center gap-2">
          {/* The fleet's version policy, next to the fleet's deploy — one opens the roll-out dialog,
              the other redeploys whatever each instance already resolves to. Only in Releases mode,
              where a version policy exists at all. */}
          {usesReleases && (
            <Button variant="secondary" onClick={() => setRollingOut(true)}>
              <Tags /> Set instances’ release…
            </Button>
          )}
          <Button
            variant="secondary"
            loading={deployAll.isPending}
            disabled={tenants.length === 0}
            onClick={() => deployAll.mutate()}
          >
            <PlayCircle /> Deploy all
          </Button>
          <Button
            variant="ghost"
            className="text-text-2 hover:text-danger"
            onClick={() => setConfirmDeleteTemplate(true)}
          >
            <Trash2 /> Delete
          </Button>
        </div>
      </div>

      <Card>
        <CardContent className="space-y-1 text-[13px] text-text-2">
          {/* The source moved onto the product (ADR-0026); the line points at where it is edited —
              as plain text when the Products module is off and that page would redirect away. */}
          <p>
            From product{' '}
            {productsEnabled ? (
              <Link
                to="/products/$id"
                params={{ id: String(template.productId) }}
                className="font-medium text-text hover:text-brand"
              >
                {template.productName}
              </Link>
            ) : (
              <span className="font-medium text-text">{template.productName}</span>
            )}{' '}
            — <span className="font-mono">{template.repositoryUrl}</span> ·{' '}
            <span className="font-mono">{template.branch}</span>
          </p>
          <p className="font-mono">{template.domainPattern} → {template.targetServiceName}:{template.targetPort}</p>
          {/* The realm decides which accounts every tenant of this template signs in with, and which
              login host they are sent to — read-only here, and the server refuses a move once the
              template has tenants. */}
          {nameOrNull(template.realmId) && <p>Realm: {nameOrNull(template.realmId)}</p>}
          <p>{baseEnvVars.length} base env var{baseEnvVars.length === 1 ? '' : 's'}</p>
        </CardContent>
      </Card>

      <Card>
        <CardContent>
          <SectionHeader title="Add tenant" description="Spins up an isolated copy on its own subdomain." />
          <div className="space-y-4">
            <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
              <Field label="Tenant slug" required hint={template.domainPattern.replace('{tenant}', slug || 'slug')}>
                {({ id: fid, describedBy }) => (
                  <Input
                    id={fid}
                    aria-describedby={describedBy}
                    mono
                    value={slug}
                    onChange={(e) => setSlug(e.target.value)}
                    placeholder="tenant1"
                    autoComplete="off"
                    spellCheck={false}
                    className="sm:w-64"
                  />
                )}
              </Field>
              <Button
                loading={addTenant.isPending}
                disabled={!slug.trim()}
                onClick={() => addTenant.mutate()}
              >
                <Plus /> Add tenant
              </Button>
            </div>
            <button
              type="button"
              className="text-[13px] text-text-2 underline-offset-2 hover:text-text hover:underline"
              onClick={() => setShowOverrides((v) => !v)}
            >
              {showOverrides ? 'Hide' : 'Add'} environment overrides
            </button>
            {showOverrides && <EnvVarEditor value={overrides} onChange={setOverrides} />}
          </div>
        </CardContent>
      </Card>

      {canManageGrants && (
        <Card>
          <CardContent>
            <SectionHeader
              title="Management API"
              description="Granted stacks may provision and manage this template's tenants through Watchtower's public Management API with their own App-API token."
            />
            <div className="space-y-4">
              {grantsQuery.isLoading ? (
                <div className="flex justify-center py-4">
                  <Spinner />
                </div>
              ) : grantsQuery.isError ? (
                <Banner tone="danger" title="Couldn’t load grants">
                  {(grantsQuery.error as Error)?.message}
                </Banner>
              ) : grants.length === 0 ? (
                <p className="text-[13px] text-text-3">No stacks are granted management access.</p>
              ) : (
                <ul className="divide-y divide-border rounded-lg border border-border">
                  {grants.map((g) => {
                    const pending = allowDeletePendingIds.has(g.stackId)
                    return (
                      <li
                        key={g.stackId}
                        className="flex flex-wrap items-center justify-between gap-3 px-3 py-2.5"
                      >
                        <div className="flex min-w-0 items-center gap-2">
                          <Link
                            to="/stacks/$id"
                            params={{ id: String(g.stackId) }}
                            className="truncate font-medium text-text hover:text-brand"
                          >
                            {g.stackName}
                          </Link>
                          {g.allowDelete && (
                            <Badge tone="warn" size="sm">
                              allow delete
                            </Badge>
                          )}
                        </div>
                        <div className="flex shrink-0 items-center gap-3">
                          <label className="flex items-center gap-2">
                            <Switch
                              checked={g.allowDelete}
                              disabled={pending}
                              onCheckedChange={(allowDelete) =>
                                setAllowDelete.mutate({ grant: g, allowDelete })
                              }
                              aria-label={`Allow ${g.stackName} to delete tenants`}
                            />
                            <span className="text-[13px] text-text-2">Allow delete</span>
                          </label>
                          <span className="tnum text-[13px] text-text-3">
                            {timeAgo(g.createdAt)}
                          </span>
                          <Tooltip label="Revoke access">
                            <Button
                              size="icon-sm"
                              variant="ghost"
                              aria-label={`Revoke ${g.stackName}`}
                              onClick={() => setPendingRevoke(g)}
                              className="text-text-2 hover:text-danger"
                            >
                              <Trash2 />
                            </Button>
                          </Tooltip>
                        </div>
                      </li>
                    )
                  })}
                </ul>
              )}

              {stacksQuery.isError && (
                <Banner tone="danger" title="Couldn’t load stacks">
                  {(stacksQuery.error as Error)?.message}
                </Banner>
              )}

              <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
                <Field
                  label="Stack"
                  className="sm:w-64"
                  hint={
                    noGrantableStacks
                      ? 'Every stack is already a tenant of this template or granted.'
                      : undefined
                  }
                >
                  {({ id: fid, describedBy }) => (
                    <Select value={grantStackId} onValueChange={setGrantStackId}>
                      <SelectTrigger
                        id={fid}
                        aria-describedby={describedBy}
                        disabled={!pickerReady || noGrantableStacks}
                      >
                        <SelectValue
                          placeholder={
                            noGrantableStacks
                              ? 'No stacks left to grant'
                              : pickerReady
                                ? 'Select a stack'
                                : pickerFailed
                                  ? 'Unavailable'
                                  : 'Loading…'
                          }
                        />
                      </SelectTrigger>
                      <SelectContent>
                        {grantableStacks.map((s) => (
                          <SelectItem key={s.id} value={String(s.id)}>
                            {s.name}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  )}
                </Field>
                <label className="flex h-9 items-center gap-3">
                  <Switch checked={grantAllowDelete} onCheckedChange={setGrantAllowDelete} />
                  <span className="text-sm text-text">Allow delete</span>
                </label>
                <Button
                  loading={grantManagement.isPending}
                  disabled={!pickerReady || !grantStackId}
                  onClick={() => grantManagement.mutate()}
                >
                  <ShieldCheck /> Grant
                </Button>
              </div>
            </div>
          </CardContent>
        </Card>
      )}

      {/* The rollup: "18 on latest · 2 pinned · 1 behind" — the "which tenant runs which version"
          answer in one line, above the table that spells it out per row. Plain text rather than a
          filter row: the three counts are already the whole answer for a fleet this page can show, and
          a filter that only ever hides rows of a 20-row table earns less than the noise it adds. The
          buckets are disjoint (a pinned-and-outdated instance counts as pinned, never as behind), so
          they sum to the roster. */}
      {usesReleases && tenants.length > 0 && (
        <p className="text-[13px] text-text-2">
          {(() => {
            const rollup = versionRollup(tenants, newestId)
            const parts = [
              rollup.onLatest > 0 && `${rollup.onLatest} on latest`,
              rollup.pinned > 0 && `${rollup.pinned} pinned`,
              rollup.behind > 0 && `${rollup.behind} behind`,
            ].filter(Boolean)
            return parts.join(' · ')
          })()}
          {template.defaultPinnedRelease && (
            <>
              {' · '}New instances start on{' '}
              <span className="font-mono text-text">{template.defaultPinnedRelease.version}</span>
            </>
          )}
        </p>
      )}

      <DataList
        items={tenants}
        getKey={(t) => t.stackId}
        columns={columns}
        renderCard={(t) => (
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-3">
              <Link to="/stacks/$id" params={{ id: String(t.stackId) }} className="font-medium text-text hover:text-brand">
                {t.tenantSlug}
              </Link>
              <div className="flex items-center gap-2">
                <StatusBadge status={t.lastDeployStatus} />
                {removeTenantButton(t)}
              </div>
            </div>
            {/* Card fallback leads with slug + version + status (design.md §Übersichtlichkeit audit). */}
            {usesReleases && <div>{versionCell(t)}</div>}
            {t.domain && <p className="font-mono text-[13px] text-text-2">{t.domain}</p>}
          </div>
        )}
        emptyState={
          <EmptyState icon={Users} title="No tenants yet" description="Add your first tenant above." />
        }
        aria-label="Tenants"
      />

      {productId != null && (
        <SetReleaseDialog
          open={rollingOut}
          onOpenChange={setRollingOut}
          productId={productId}
          // Seeded from where the fleet already is, so the dialog opens describing the status quo
          // rather than proposing a change nobody asked for: opening it on "Track latest" over a fleet
          // pinned to 1.3.0 makes Apply an unpin by accident.
          seedReleaseId={template.defaultPinnedRelease?.id ?? null}
          fleet={{ templateId, templateName: template.name }}
          targets={tenants.map(
            (t): ReleaseTarget => ({ stackId: t.stackId, label: t.tenantSlug, state: t }),
          )}
        />
      )}

      <ConfirmDialog
        open={pendingRemoveTenant != null}
        onOpenChange={(open) => {
          if (!open && !removeTenant.isPending) setPendingRemoveTenant(null)
        }}
        title={pendingRemoveTenant ? `Remove ${pendingRemoveTenant.tenantSlug}?` : 'Remove tenant?'}
        description="This permanently deletes the tenant's stack, its route, its environment and its deployment history, and removes its containers. Cannot be undone."
        extra={
          <label className="flex items-center gap-3">
            <Switch
              checked={removeVolumes}
              onCheckedChange={setRemoveVolumes}
              disabled={removeTenant.isPending}
            />
            <span className="text-sm text-text">Also remove volumes (destroys tenant data)</span>
          </label>
        }
        confirmLabel="Remove"
        tone="danger"
        requireText={pendingRemoveTenant?.tenantSlug}
        loading={removeTenant.isPending}
        onConfirm={() => {
          if (pendingRemoveTenant) removeTenant.mutate(pendingRemoveTenant)
        }}
      />

      <ConfirmDialog
        open={pendingRevoke != null}
        onOpenChange={(open) => {
          if (!open && !revokeManagement.isPending) setPendingRevoke(null)
        }}
        title={pendingRevoke ? `Revoke ${pendingRevoke.stackName}?` : 'Revoke management access?'}
        description="That stack's App-API token will stop being accepted by this template's Management API. Tenants it created keep running."
        confirmLabel="Revoke"
        tone="danger"
        loading={revokeManagement.isPending}
        onConfirm={() => {
          if (pendingRevoke) revokeManagement.mutate(pendingRevoke)
        }}
      />

      <ConfirmDialog
        open={confirmDeleteTemplate}
        onOpenChange={(open) => {
          if (!open && !removeTemplate.isPending) setConfirmDeleteTemplate(false)
        }}
        title={`Delete ${template.name}?`}
        description="Existing tenants keep running; they're just detached from this template."
        confirmLabel="Delete"
        tone="danger"
        loading={removeTemplate.isPending}
        onConfirm={() => removeTemplate.mutate()}
      />
    </div>
  )
}
