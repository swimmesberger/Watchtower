import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Archive, ChevronDown, ChevronRight, Play } from 'lucide-react'
import { api } from '@/lib/api'
import type {
  BackupProductRollup,
  BackupQuiesceMode,
  BackupTemplatePolicy,
  Product,
} from '@/lib/types'
import { describeCron } from '@/lib/cron'
import { absoluteTitle, formatBytes, formatDuration, timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
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
import { Skeleton } from '@/components/ui/skeleton'
import { StatusBadge } from '@/components/ui/status-badge'
import { toast } from '@/components/ui/use-toast'
import { cn } from '@/lib/utils'
import {
  BackupOverrideMenu,
  describeOverride,
  type BackupOverrideValue,
} from './BackupOverrideMenu'

// ── Product Backups tab (ADR-0026 stage 7) ──────────────────────────────────────
//
// design.md §"Backups across tenants": the template policy card, the fleet rollup and the fleet
// history. The per-stack surface — run now, restore, the plan preview — stays on the stack's own
// Backups tab, because those are things you do to one running copy.
//
// The one word this whole tab hangs on is *inherit*. A template's policy is not copied onto its
// instances; it is read through, live, every time. So the card writes one row, the rollup says what
// that is actually achieving, and the "N of them have their own settings" line is the honest caveat.

/** The sentinel a tri-state select uses for "no opinion" — the empty string is not a usable value. */
const INHERIT = 'inherit'

export function ProductBackupsTab({ product }: { product: Product }) {
  const {
    data,
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['backups', 'product', product.id],
    queryFn: () => api.backups.getProductBackups(product.id),
  })

  const { data: events = [], isLoading: eventsLoading } = useQuery({
    queryKey: ['backups', 'product-events', product.id],
    queryFn: () => api.backups.events(undefined, 50, product.id),
    refetchInterval: (q) =>
      q.state.data?.some((e) => e.status === 'running' || e.status === 'queued') ? 3000 : 15_000,
  })

  return (
    <div className="space-y-6">
      <SectionHeader title="Backups" />
      <p className="-mt-4 text-[13px] text-text-2">
        The backup policy every instance of this product inherits, and how its deployments are
        actually doing. Storage, encryption and retention are instance-wide —{' '}
        <Link to="/settings" className="underline underline-offset-2 hover:text-text">
          Settings → Backups
        </Link>
        .
      </p>

      {isLoading ? (
        <Card>
          <CardContent className="flex flex-col gap-4">
            <Skeleton variant="line" className="w-2/3" />
            <Skeleton variant="line" className="w-1/2" />
          </CardContent>
        </Card>
      ) : isError || !data ? (
        <Banner
          tone="danger"
          title="Couldn’t load this product’s backups"
          action={
            <Button variant="secondary" size="sm" onClick={() => refetch()}>
              Retry
            </Button>
          }
        />
      ) : (
        <>
          {!data.scheduleEnabled && (
            <Banner tone="warn" title="The backup schedule is off">
              Nothing runs automatically, whatever this policy says. Manual and pre-deploy backups
              still work; turn the schedule on under Settings → Backups.
            </Banner>
          )}

          <RollupCard rollup={data.rollup} />

          {data.templates.length === 0 ? (
            <Card>
              <CardContent>
                <p className="text-[13px] text-text-2">
                  This product has no tenancy yet, so there is no fleet policy to set. Each
                  deployment configures its own backups on its Backups tab.
                </p>
              </CardContent>
            </Card>
          ) : (
            data.templates.map((policy) => (
              <TemplatePolicyCard
                key={policy.templateId}
                productId={product.id}
                policy={policy}
                instanceCron={data.instanceCron}
              />
            ))
          )}
        </>
      )}

      <div>
        <SectionHeader title="History" description="Every backup run across this product’s deployments." />
        {eventsLoading ? (
          <div className="space-y-3">
            <Skeleton variant="rect" className="h-12 w-full" />
            <Skeleton variant="rect" className="h-12 w-full" />
          </div>
        ) : events.length === 0 ? (
          <EmptyState
            icon={Archive}
            title="No backups yet"
            description="Nothing in this product has been backed up. Turn the fleet policy on, or run one from a deployment’s Backups tab."
          />
        ) : (
          <div className="space-y-2">
            {events.map((e) => (
              <FleetHistoryRow key={e.id} event={e} />
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * "19 backed up in the last 24 h · 1 failed · 2 never". The question the tab exists to answer is "is
 * this fleet actually being backed up", and a list of runs cannot answer it — a stack with no runs at
 * all contributes no rows to look at.
 *
 * **The buckets partition the *enrolled* stacks, and only those.** A stack nobody put in the schedule
 * is not failing at anything, so it is counted apart and rendered neutrally; putting it in "never"
 * would paint a deliberate choice red forever. The four that do partition are rendered in the same
 * priority order the server assigns them, so the line adds up.
 */
function RollupCard({ rollup }: { rollup: BackupProductRollup }) {
  if (rollup.deployments === 0) {
    return (
      <Card>
        <CardContent>
          <p className="text-[13px] text-text-2">No deployments yet — nothing to back up.</p>
        </CardContent>
      </Card>
    )
  }
  if (rollup.enrolled === 0) {
    return (
      <Card>
        <CardContent>
          <p className="text-[13px] text-text-2">
            None of this product’s {rollup.deployments} deployment
            {rollup.deployments === 1 ? ' is' : 's are'} in the backup schedule. Turn the fleet policy
            on below, or include a deployment from its own Backups tab.
          </p>
        </CardContent>
      </Card>
    )
  }
  return (
    <Card>
      <CardContent className="flex flex-wrap items-center gap-x-6 gap-y-2">
        <Stat
          value={rollup.backedUpRecently}
          label={`backed up in the last ${rollup.windowHours} h`}
          tone={rollup.backedUpRecently === rollup.enrolled ? 'ok' : 'neutral'}
        />
        {/* Shown only when it is not zero: "0 stale" on a healthy fleet is a number that never says
            anything, and the three the design names are the ones that always earn their place. */}
        {/* "older than that" reads off the label before it — "3 backed up in the last 24 h · 1 older
            than that" — where "1 stale" would need a glossary and "1 not backed up since" is not a
            sentence. */}
        {rollup.stale > 0 && <Stat value={rollup.stale} label="older than that" tone="neutral" />}
        <Stat value={rollup.failed} label="failed" tone={rollup.failed > 0 ? 'danger' : 'neutral'} />
        <Stat value={rollup.never} label="never" tone={rollup.never > 0 ? 'warn' : 'neutral'} />
        <span className="ml-auto text-[13px] text-text-3">
          {rollup.enrolled} in the schedule
          {rollup.notEnrolled > 0 && <> · {rollup.notEnrolled} not</>}
        </span>
      </CardContent>
    </Card>
  )
}

function Stat({ value, label, tone }: { value: number; label: string; tone: 'ok' | 'warn' | 'danger' | 'neutral' }) {
  return (
    <span className="flex items-baseline gap-1.5">
      <span
        className={cn(
          'tnum text-lg font-semibold',
          tone === 'ok' && 'text-ok',
          tone === 'warn' && 'text-warn',
          tone === 'danger' && 'text-danger',
          tone === 'neutral' && 'text-text',
        )}
      >
        {value}
      </span>
      <span className="text-[13px] text-text-2">{label}</span>
    </span>
  )
}

/** The four editable fields of a fleet policy, as the form holds them (strings, `INHERIT` for null). */
interface PolicyForm {
  enabled: string
  stopContainers: string
  quiesceMode: string
  cron: string
}

/** The fetched policy as a form. One place, so seeding and dirty-checking cannot disagree. */
function formOf(policy: BackupTemplatePolicy): PolicyForm {
  return {
    enabled: triState(policy.enabled),
    stopContainers: triState(policy.stopContainers),
    quiesceMode: policy.quiesceMode ?? INHERIT,
    cron: policy.cron ?? '',
  }
}

/** A comparable signature of a form — four short strings, so `JSON.stringify` is the honest cheap way. */
const signature = (form: PolicyForm) => JSON.stringify(form)

/**
 * The fleet policy. Every control is tri-state and defaults to **Inherit**, because a template that
 * says nothing is the honest starting state — the instance default already applies, and a card that
 * opened on "off" would look like a decision nobody made. The whole policy is posted on every save
 * (the server clears what is not sent), so all four fields go together.
 *
 * **Seeded once, re-seeded on a refetch that the reader is not in the middle of overwriting.** This
 * component does not remount when the query refetches, so a form seeded only at mount goes stale the
 * moment the policy moves behind the page — and, worse, the *dirty* flag would turn true by itself and
 * the next Save would post the stale mount values back over it. That is the stage-6 `releaseMode` trap
 * exactly. Both halves are fixed the way that pattern prescribes: dirtiness is measured against what
 * the form was last **seeded** from (a ref), never against the live prop, and the effect re-seeds only
 * a form the reader has not touched — an edit in progress is left alone rather than clobbered.
 */
function TemplatePolicyCard({
  productId,
  policy,
  instanceCron,
}: {
  productId: number
  policy: BackupTemplatePolicy
  instanceCron: string
}) {
  const qc = useQueryClient()
  const [form, setForm] = useState<PolicyForm>(() => formOf(policy))
  /** What the form was last seeded from — the baseline `dirty` is measured against. */
  const seeded = useRef<PolicyForm>(form)
  /** The live form, readable inside the effect without making it a dependency (which would loop). */
  const current = useRef<PolicyForm>(form)
  current.current = form

  const set = <K extends keyof PolicyForm>(key: K, value: PolicyForm[K]) =>
    setForm((previous) => ({ ...previous, [key]: value }))

  const fetched = signature(formOf(policy))
  useEffect(() => {
    const next = formOf(policy)
    const baseline = signature(seeded.current)
    if (baseline === signature(next)) return
    // The dirty guard. Re-seeding a form somebody is typing into would throw their edit away; leaving
    // a *clean* form stale would show a policy that is no longer true and post it back on the next save.
    if (signature(current.current) !== baseline) return
    seeded.current = next
    setForm(next)
    // `fetched` is the signature, so this fires when the values move and not on every render that
    // happens to hand down a new object identity.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fetched])

  const save = useMutation({
    mutationFn: () =>
      api.backups.setTemplatePolicy(
        policy.templateId,
        form.enabled === INHERIT ? null : form.enabled === 'true',
        form.stopContainers === INHERIT ? null : form.stopContainers === 'true',
        form.cron.trim() === '' ? null : form.cron.trim(),
        form.quiesceMode === INHERIT ? null : (form.quiesceMode as BackupQuiesceMode),
      ),
    onSuccess: (saved) => {
      // Seed from what the server stored, not from what was typed: the cron is trimmed on the way in,
      // so the form and the row would otherwise disagree by a space and the card would stay "dirty".
      const next = formOf(saved)
      seeded.current = next
      setForm(next)
      qc.invalidateQueries({ queryKey: ['backups', 'product', productId] })
      // Every instance's own Backups tab reads the ladder this just moved.
      qc.invalidateQueries({ queryKey: ['backups', 'stack-config'] })
      toast.success(`Saved ${policy.templateName}’s backup policy.`)
    },
    // The server's refusal names the bad cron field-by-field; nothing here improves on it.
    onError: (err: Error) => toast.error('Couldn’t save the policy', err.message),
  })

  const backupAll = useMutation({
    mutationFn: () => api.templates.backupAll(policy.templateId),
    onSuccess: (count) => {
      qc.invalidateQueries({ queryKey: ['backups', 'product-events', productId] })
      toast.info(
        count === 0 ? 'No instances to back up.' : `Backing up ${count} instance${count === 1 ? '' : 's'}…`,
        // The duration expectation, stated where it is decided (design.md §Risks, open question 12).
        count > 1 ? 'Backups run one at a time, so the last one finishes well after the first.' : undefined,
      )
    },
    onError: (err: Error) => toast.error('Couldn’t start the backups', err.message),
  })

  // Measured against what the form was seeded from, never against the live prop — see the remarks.
  const dirty = signature(form) !== signature(seeded.current)

  const cronPreview =
    form.cron.trim() === ''
      ? `Inherited: the instance schedule (${describeCron(instanceCron) ?? instanceCron}).`
      : (describeCron(form.cron.trim()) ?? 'Custom expression — shown as entered.')

  return (
    <Card>
      <CardContent className="space-y-5">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="text-sm font-medium text-text">{policy.templateName}</p>
            <p className="mt-0.5 text-[13px] text-text-2">
              {policy.tenantCount === 0
                ? 'No instances yet — this policy applies to the ones created later.'
                : `Applies to ${policy.tenantCount} instance${policy.tenantCount === 1 ? '' : 's'}, live.`}
              {policy.overriddenTenantCount > 0 && (
                <>
                  {' '}
                  <span className="text-warn">
                    {policy.overriddenTenantCount} of them {policy.overriddenTenantCount === 1 ? 'has' : 'have'}{' '}
                    settings of their own and will not follow this.
                  </span>
                </>
              )}
            </p>
          </div>
          <Button
            variant="secondary"
            size="sm"
            className="shrink-0"
            loading={backupAll.isPending}
            disabled={backupAll.isPending || policy.tenantCount === 0}
            onClick={() => backupAll.mutate()}
          >
            {!backupAll.isPending && <Play />}
            Back up all instances
          </Button>
        </div>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field label="Include instances in the schedule">
            {({ id }) => (
              <TriStateSelect
                id={id}
                value={form.enabled}
                onChange={(v) => set('enabled', v)}
                inheritLabel="Inherit — off (instance default)"
                onLabel="On"
                offLabel="Off"
              />
            )}
          </Field>

          <Field label="Stop stateful containers for the snapshot">
            {({ id }) => (
              <TriStateSelect
                id={id}
                value={form.stopContainers}
                onChange={(v) => set('stopContainers', v)}
                inheritLabel="Inherit — on (instance default)"
                onLabel="On"
                offLabel="Off"
              />
            )}
          </Field>

          <Field
            label="Quiesce mode"
            hint="How the stateful containers are taken out of service."
          >
            {({ id, describedBy }) => (
              <Select value={form.quiesceMode} onValueChange={(v) => set('quiesceMode', v)}>
                <SelectTrigger id={id} aria-describedby={describedBy}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={INHERIT}>Inherit — stop (instance default)</SelectItem>
                  <SelectItem value="stop">Stop (application-consistent)</SelectItem>
                  <SelectItem value="pause">Pause (crash-consistent, seconds of downtime)</SelectItem>
                </SelectContent>
              </Select>
            )}
          </Field>

          <Field label="Schedule" hint="Five fields, server-local time. Empty inherits.">
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                mono
                value={form.cron}
                onChange={(e) => set('cron', e.target.value)}
                placeholder={instanceCron}
                spellCheck={false}
              />
            )}
          </Field>
        </div>

        <p className="text-[13px] text-text-2">{cronPreview}</p>

        <div className="flex items-center gap-3">
          <Button
            size="sm"
            variant="secondary"
            loading={save.isPending}
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate()}
          >
            Save policy
          </Button>
          {dirty && <span className="text-[13px] text-text-3">Unsaved changes</span>}
        </div>

        <TemplateServiceOverrides productId={productId} policy={policy} />
      </CardContent>
    </Card>
  )
}

/**
 * The fleet's per-service overrides — "never back up the cache service" written once instead of once
 * per tenant (design.md §"Backups across tenants", the honest v2 the table shipped without).
 *
 * **It borrows one instance's service list, and that is the whole design.** A fleet has no containers
 * of its own, so there is nothing to enumerate services from; the plan preview of a *tenant* is the
 * closest thing, and it is the same table the stack tab already renders. The instance is named and
 * pickable rather than implicit, because "these are the services" is a claim borrowed from one running
 * copy and the reader has to be able to see whose.
 *
 * Two rules keep it honest. The current value of a row is the **template's own** row from
 * `policy.serviceOverrides`, never the borrowed preview's `override` — the preview shows the donor's
 * *effective* ladder, so a donor that overrides a service itself would hide the fleet's setting behind
 * its own. And a template row whose service is absent from the borrowed list still gets a row, so a
 * setting for a service that has been renamed away can still be found and cleared.
 *
 * These rows do **not** go into the compose-label snippet on the stack tab: that snippet's contract is
 * "paste this, delete your overrides, nothing changes", which an instance cannot honour for a row it
 * merely inherits (stage 7's review round).
 */
function TemplateServiceOverrides({
  productId,
  policy,
}: {
  productId: number
  policy: BackupTemplatePolicy
}) {
  const qc = useQueryClient()
  // The same key the Instances tab uses, so the two share one cache entry.
  const { data: tenants = [], isLoading: tenantsLoading } = useQuery({
    queryKey: ['tenants', policy.templateId],
    queryFn: () => api.templates.listTenants(policy.templateId),
  })

  // A tenant that has deployed at least once is the one whose preview can actually list services;
  // failing that, any tenant, so the picker is never empty for a fleet that has instances.
  const deployed = tenants.filter((t) => t.lastDeployedAt != null)
  const candidates = deployed.length > 0 ? deployed : tenants
  const [donorId, setDonorId] = useState<number | null>(null)
  const donor = candidates.find((t) => t.stackId === donorId) ?? candidates[0] ?? null

  // The same key the stack's own Backups tab uses — reading a service list here costs nothing extra
  // for a reader who then opens that instance.
  const {
    data: preview,
    isLoading: previewLoading,
    isError: previewFailed,
  } = useQuery({
    queryKey: ['backups', 'plan-preview', donor?.stackId],
    queryFn: () => api.backups.previewPlan(donor!.stackId),
    enabled: donor != null,
    staleTime: 15_000,
    retry: false,
  })

  const setOverride = useMutation({
    mutationFn: (next: { service: string } & BackupOverrideValue) =>
      api.backups.setTemplateServiceOverride(policy.templateId, next.service, {
        exclude: next.exclude,
        stop: next.stop,
        dump: next.dump,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['backups', 'product', productId] })
      // Every instance reads this rung live, and their previews render it as "Template policy: …".
      qc.invalidateQueries({ queryKey: ['backups', 'plan-preview'] })
      toast.success(`Saved ${policy.templateName}’s service settings.`)
    },
    onError: (err: Error) => toast.error('Couldn’t save', err.message),
  })

  const stored = new Map(policy.serviceOverrides.map((o) => [o.service, o]))
  const fromPreview = (preview?.services ?? []).map((s) => s.service)
  // Union, preview order first: a stored row whose service is gone from the donor must stay reachable.
  const services = [
    ...fromPreview,
    ...policy.serviceOverrides.map((o) => o.service).filter((s) => !fromPreview.includes(s)),
  ]

  const loading = tenantsLoading || (donor != null && previewLoading)

  return (
    <div className="space-y-3 border-t border-border pt-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-medium text-text">Per-service settings</p>
          <p className="mt-0.5 text-[13px] text-text-2">
            Applies to every tenant; a tenant’s own override or a{' '}
            <code className="font-mono text-[12px]">watchtower.backup.*</code> compose label still
            wins.
          </p>
        </div>
        {/* Named, not implicit: the service list is borrowed from one running copy, and which one is
            a fact the reader is entitled to. Only a picker when there is something to pick. */}
        {candidates.length > 1 && donor && (
          <Select value={String(donor.stackId)} onValueChange={(v) => setDonorId(Number(v))}>
            <SelectTrigger className="w-auto min-w-[12rem]" aria-label="Instance to read the service list from">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {candidates.map((t) => (
                <SelectItem key={t.stackId} value={String(t.stackId)}>
                  Services of {t.tenantSlug}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </div>

      {loading ? (
        <Skeleton variant="rect" className="h-16 w-full" />
      ) : services.length === 0 ? (
        <p className="text-[13px] text-text-3">
          {tenants.length === 0
            ? 'Add a tenant to see its services — or set overrides as compose labels, which win anyway.'
            : previewFailed
              ? `Couldn’t read ${donor?.tenantSlug}’s services — the Docker daemon has to be reachable. Compose labels work regardless, and win anyway.`
              : 'Start a tenant to see its services — or set overrides as compose labels, which win anyway.'}
        </p>
      ) : (
        <>
          <ul className="divide-y divide-border rounded-lg border border-border">
            {services.map((service) => {
              const row = stored.get(service) ?? null
              const value: BackupOverrideValue | null = row
                ? { exclude: row.exclude, stop: row.stop ?? null, dump: row.dump ?? null }
                : null
              const described = describeOverride(value)
              return (
                <li
                  key={service}
                  className="flex flex-wrap items-center justify-between gap-3 px-3 py-2"
                >
                  <span className="flex min-w-0 flex-col">
                    <span className="truncate font-medium text-[13px] text-text">{service}</span>
                    <span className="truncate text-[12px] text-text-3">
                      {described ?? 'no fleet setting'}
                      {!fromPreview.includes(service) && ' · not in the listed instance'}
                    </span>
                  </span>
                  <BackupOverrideMenu
                    value={value}
                    pending={setOverride.isPending}
                    scopeDefaultLabel="Fleet default"
                    onChange={(next) => setOverride.mutate({ service, ...next })}
                  />
                </li>
              )
            })}
          </ul>
          {/* Only claimed when the borrowed list actually produced something. A preview that failed
              (no reachable daemon, an instance that has never deployed) leaves only the stored rows on
              screen, and saying they were "read from" an instance would be the opposite of true. */}
          {donor && fromPreview.length > 0 ? (
            <p className="text-[12px] text-text-3">
              Services read from{' '}
              <Link
                to="/stacks/$id"
                params={{ id: String(donor.stackId) }}
                className="underline underline-offset-2 hover:text-text"
              >
                {donor.tenantSlug}
              </Link>
              . A service only some instances run can still be set here once one of them lists it.
            </p>
          ) : (
            <p className="text-[12px] text-text-3">
              {donor
                ? `Couldn’t read ${donor.tenantSlug}’s services, so only the settings already stored are listed.`
                : 'No instance to read a service list from, so only the settings already stored are listed.'}
            </p>
          )}
        </>
      )}
    </div>
  )
}

/** A nullable boolean as three options; the sentinel is a word because "" is not a usable select value. */
function TriStateSelect({
  id,
  value,
  onChange,
  inheritLabel,
  onLabel,
  offLabel,
}: {
  id: string
  value: string
  onChange: (value: string) => void
  inheritLabel: string
  onLabel: string
  offLabel: string
}) {
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger id={id}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={INHERIT}>{inheritLabel}</SelectItem>
        <SelectItem value="true">{onLabel}</SelectItem>
        <SelectItem value="false">{offLabel}</SelectItem>
      </SelectContent>
    </Select>
  )
}

/**
 * A nullable boolean as a select value. `!= null` rather than `!== null`: the API omits nulls, so an
 * unset field arrives as `undefined`.
 */
function triState(value: boolean | null | undefined): string {
  return value != null ? String(value) : INHERIT
}

/** One fleet-history row: which instance, status, trigger, age, size — expanded shows the run log. */
function FleetHistoryRow({
  event,
}: {
  event: {
    id: number
    stackId: number
    stackName: string
    triggeredBy: string
    status: string
    remotePath: string | null
    sizeBytes: number | null
    output: string | null
    startedAt: string
    finishedAt: string | null
  }
}) {
  const [expanded, setExpanded] = useState(false)
  const isActive = event.status === 'running' || event.status === 'queued'

  return (
    <div className="overflow-hidden rounded-lg border border-border bg-surface">
      <button
        type="button"
        onClick={() => setExpanded((e) => !e)}
        aria-expanded={expanded}
        className={cn(
          'flex w-full flex-wrap items-center gap-x-3 gap-y-1.5 px-4 py-3 text-left',
          'transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
        )}
      >
        {expanded ? (
          <ChevronDown className="size-4 shrink-0 text-text-3" aria-hidden />
        ) : (
          <ChevronRight className="size-4 shrink-0 text-text-3" aria-hidden />
        )}
        <StatusBadge status={event.status} size="sm" />
        {/* Which instance — the column a per-stack history has no need of and a fleet history cannot
            do without. */}
        <span className="min-w-0 truncate text-[13px] font-medium text-text">{event.stackName}</span>
        <Badge tone="neutral" size="sm">
          {event.triggeredBy}
        </Badge>
        <span className="tnum text-xs text-text-2" title={absoluteTitle(event.startedAt)}>
          {timeAgo(event.startedAt)}
        </span>
        {event.sizeBytes != null && (
          <span className="tnum text-xs text-text-2">{formatBytes(event.sizeBytes)}</span>
        )}
        <span className="tnum ml-auto text-xs text-text-3">
          {formatDuration(event.startedAt, event.finishedAt)}
        </span>
        {isActive && (
          <span className="inline-flex items-center gap-1.5 text-[11px] font-semibold text-run">
            <span
              className="size-1.5 rounded-full bg-current motion-safe:animate-[wt-live_1.4s_ease-in-out_infinite]"
              aria-hidden
            />
            live
          </span>
        )}
      </button>

      {expanded && (
        <div className="border-t border-border p-3">
          {event.remotePath && (
            <p className="mb-2 break-all font-mono text-[12px] text-text-2">{event.remotePath}</p>
          )}
          <pre className="max-h-72 overflow-auto whitespace-pre-wrap rounded-md bg-surface-2 p-3 font-mono text-[12px] leading-relaxed text-text-2">
            {event.output ?? (isActive ? 'Running…' : 'No output recorded.')}
          </pre>
        </div>
      )}
    </div>
  )
}
