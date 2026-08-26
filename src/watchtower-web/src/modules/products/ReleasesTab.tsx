import { useState } from 'react'
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  ChevronDown,
  ChevronRight,
  ExternalLink,
  Package,
  Plus,
  RotateCcw,
  RotateCw,
  Tags,
  Trash2,
} from 'lucide-react'
import { api } from '@/lib/api'
import type { CiLink, Product, ProductStack, ProductTemplate, Release } from '@/lib/types'
import { absoluteTitle, timeAgo, truncateMiddle } from '@/lib/format'
import { commitUrl } from '@/lib/source'
import { SetReleaseDialog, type ReleaseTarget } from '@/components/set-release-dialog'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { CopyButton } from '@/components/ui/copy-button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { SecretField } from '@/components/ui/secret-field'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'
import { DeployLatestButton } from './DeployLatestButton'

// ── Releases tab — one build of this product (ADR-0026 stage 3) ─────────────────
//
// Read-only in the deployment sense: releases accumulate and are visible, and nothing here deploys
// anything. The version policy — pin, roll out, roll back — arrives with stage 4.

/** The definition sentence, from docs/products/design.md's Releases empty state. */
const RELEASE_DEFINITION =
  'A release is one build of this product: the git commit plus the image digests your CI produced.'

export function ReleasesTab({ product }: { product: Product }) {
  const [recording, setRecording] = useState(false)

  // Shares the detail page's cache entry, so a rotation refreshes both.
  const { data: detail } = useQuery({
    queryKey: ['product', product.id],
    queryFn: () => api.products.get(product.id),
  })

  const releases = useInfiniteQuery({
    queryKey: ['product', product.id, 'releases'],
    queryFn: ({ pageParam }) => api.products.listReleases(product.id, pageParam),
    initialPageParam: undefined as number | undefined,
    // Keyset, not offset: the next page starts below the last id this page showed.
    getNextPageParam: (last) =>
      last.hasMore ? last.releases[last.releases.length - 1]?.id : undefined,
  })

  // The CI link, for the one question this tab asks of it: does Watchtower already put the token in
  // the repository, or does the operator still have to? Same query key as the CI tab, so the two
  // share one cache entry and neither pays for the other's read.
  const ci = useQuery({
    queryKey: ['product', product.id, 'ci'],
    queryFn: () => api.ci.getProductCi(product.id),
  })

  const rows = releases.data?.pages.flatMap((p) => p.releases) ?? []
  const newestId = rows[0]?.id

  // The rosters, for the per-row action: which stacks it would move, and whether this product has a
  // template whose fleet default the dialog can write. Same query key as the detail page, so the tab
  // reads the cache the page already primed.
  const stacks = detail?.stacks ?? []
  const templates = detail?.templates ?? []

  return (
    <div className="space-y-6">
      <SectionHeader
        title="Releases"
        description="Newest first. Each one pins the commit and the image digests a build produced."
        action={
          <div className="flex items-center gap-2">
            <Button variant="secondary" size="sm" onClick={() => setRecording(true)}>
              <Plus /> Record release manually
            </Button>
            {/* Header-level, not per-row: see the note on DeployLatestButton. */}
            {product.releaseMode === 'releases' && (
              <DeployLatestButton product={product} label="Deploy latest to all" size="sm" />
            )}
          </div>
        }
      />

      {releases.isError && (
        <Banner
          tone="danger"
          title="Couldn’t load releases"
          action={
            <Button variant="secondary" size="sm" onClick={() => releases.refetch()}>
              Retry
            </Button>
          }
        >
          {(releases.error as Error)?.message}
        </Banner>
      )}

      {releases.isLoading ? (
        <Skeleton variant="rect" className="h-40 w-full" />
      ) : rows.length === 0 ? (
        <EmptyState icon={Package} title="No releases yet" description={RELEASE_DEFINITION} />
      ) : (
        <>
          <ul className="divide-y divide-border rounded-lg border border-border">
            {rows.map((release) => (
              <ReleaseRow
                key={release.id}
                product={product}
                release={release}
                isLatest={release.id === newestId}
                stacks={stacks}
                templates={templates}
              />
            ))}
          </ul>
          {releases.hasNextPage && (
            <div className="flex justify-center">
              <Button
                variant="secondary"
                size="sm"
                loading={releases.isFetchingNextPage}
                onClick={() => releases.fetchNextPage()}
              >
                Show older
              </Button>
            </div>
          )}
        </>
      )}

      <ReportFromCiCard
        product={product}
        token={detail?.releaseWebhookToken ?? null}
        hasReleases={rows.length > 0}
        // Both answers, or neither: the card's whole shape depends on the CI link, and rendering the
        // manual instructions first and pulling them away a beat later is the stage-3 flicker again.
        ready={releases.isSuccess && !ci.isPending}
        ci={ci.data ?? null}
      />

      <RecordReleaseDialog product={product} open={recording} onOpenChange={setRecording} />
    </div>
  )
}

/**
 * What the row's action is called, which is a fact about where the fleet already is
 * (design.md §"Product detail page": "Row menu labels are contextual … so the consequence is stated
 * before the click").
 *
 * **The reference is the fleet, not the release list.** A product whose CI published three releases
 * nobody rolled out yet is still *on* the first one, so calling the second "roll back" because a third
 * exists would describe the version list rather than the consequence of the click. Each deployment's
 * own version is its pin when it has one and its last deployed release otherwise — the same
 * `pin ?? deployed` rule every version surface reads — and the fleet's position is the newest of those.
 * Ids are the ordering (invariant 7).
 *
 * A fleet that is nowhere yet (nothing pinned, nothing deployed) has nothing to be newer or older than,
 * and so does the release the fleet is already on: both get the neutral label.
 */
function rowAction(release: Release, stacks: ProductStack[]): 'deploy' | 'rollback' | 'set' {
  const positions = stacks
    .map((s) => (s.pinnedRelease ?? s.lastDeployedRelease)?.id)
    .filter((id): id is number => id != null)
  if (positions.length === 0) return 'set'
  const fleetAt = Math.max(...positions)
  if (release.id > fleetAt) return 'deploy'
  if (release.id < fleetAt) return 'rollback'
  return 'set'
}

// "Roll out", not "Deploy": the click opens the roll-out dialog, which pins a chosen set of instances
// — it does not deploy anything by itself, and the dialog's own Deploy-now checkbox can be turned off.
// A button labelled "Deploy this release" promises the one thing the dialog does not necessarily do.
// The ellipsis is the same signal every other dialog-opening control on these pages uses.
const ROW_ACTION_LABEL = {
  deploy: 'Roll out this release…',
  rollback: 'Roll back to this release…',
  set: 'Set this release…',
} as const

/** One release: the row everything is read off, expanding to the images it pins. */
function ReleaseRow({
  product,
  release,
  isLatest,
  stacks,
  templates,
}: {
  product: Product
  release: Release
  isLatest: boolean
  /** The product's deployments — what the row action would move, and where they are now. */
  stacks: ProductStack[]
  /** Its templates; the first one's fleet default is what a full selection would write. */
  templates: ProductTemplate[]
}) {
  const qc = useQueryClient()
  const [expanded, setExpanded] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [settingRelease, setSettingRelease] = useState(false)

  const remove = useMutation({
    mutationFn: () => api.products.deleteRelease(release.id),
    onSuccess: () => {
      setConfirming(false)
      qc.invalidateQueries({ queryKey: ['product', product.id] })
      toast.success(`Release ${release.version} deleted.`)
    },
    onError: (err: Error) => toast.error('Couldn’t delete the release', err.message),
  })

  const href = release.commitSha ? commitUrl(product.repositoryUrl, release.commitSha) : null
  const shortSha = release.commitSha?.slice(0, 7)
  const action = rowAction(release, stacks)
  // A tenant is labelled by its slug; a standalone deployment by its stack name. Both are stacks of
  // this product, and both are things the row action can move.
  const targets = stacks.map(
    (s): ReleaseTarget => ({ stackId: s.id, label: s.tenantSlug ?? s.name, state: s }),
  )
  // The fleet path is offered only when *every* deployment of this product is a tenant of the one
  // template. `templates.setTenantsRelease` writes that template's tenants and its default and nothing
  // else, so a product that also has standalone stacks would have them silently missed by a
  // "select all" that looked like it covered them. Then the per-stack path covers everything instead,
  // and the fleet default is set from the template's own Instances roster.
  const onlyTemplate = templates.length === 1 ? templates[0] : undefined
  const fleet =
    onlyTemplate && stacks.length > 0 && stacks.every((s) => s.templateId === onlyTemplate.id)
      ? { templateId: onlyTemplate.id, templateName: onlyTemplate.name }
      : null

  return (
    <li>
      <div className="flex flex-wrap items-center gap-3 px-3 py-2.5">
        <button
          type="button"
          onClick={() => setExpanded((v) => !v)}
          aria-expanded={expanded}
          className="flex min-w-0 flex-1 items-center gap-2 rounded text-left transition-colors hover:text-brand focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
        >
          {expanded ? (
            <ChevronDown className="size-4 shrink-0 text-text-3" aria-hidden />
          ) : (
            <ChevronRight className="size-4 shrink-0 text-text-3" aria-hidden />
          )}
          <span className="truncate font-medium text-text">{release.version}</span>
          {isLatest && (
            <Badge tone="brand" size="sm">
              latest
            </Badge>
          )}
        </button>

        <div className="flex shrink-0 items-center gap-3 text-[13px] text-text-3">
          {shortSha &&
            (href ? (
              <a
                href={href}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-1 font-mono text-text-2 hover:text-brand"
                title={release.commitSha!}
              >
                {shortSha}
                <ExternalLink className="size-3" aria-hidden />
              </a>
            ) : (
              <span className="font-mono text-text-2" title={release.commitSha!}>
                {shortSha}
              </span>
            ))}
          <span className="tnum" title={absoluteTitle(release.createdAt)}>
            {timeAgo(release.createdAt)}
          </span>
          <Badge tone="neutral" size="sm">
            {release.createdVia}
          </Badge>
          <span className="tnum">
            {release.imageCount} image{release.imageCount === 1 ? '' : 's'}
          </span>
          {/* The contextual action. Only in Releases mode and only with something to move: a Git-mode
              product's stacks deploy branch heads, and the backend refuses a pin outright. */}
          {product.releaseMode === 'releases' && stacks.length > 0 && (
            <Button variant="ghost" size="sm" onClick={() => setSettingRelease(true)}>
              <Tags /> {ROW_ACTION_LABEL[action]}
            </Button>
          )}
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`Delete release ${release.version}`}
            onClick={() => setConfirming(true)}
          >
            <Trash2 />
          </Button>
        </div>
      </div>

      {expanded && (
        <div className="border-t border-border bg-surface-2">
          <ReleaseRolloutSummary release={release} />
          <ReleaseImages releaseId={release.id} />
        </div>
      )}

      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Delete release ${release.version}?`}
        description="The record of this build is removed, including the image digests it pins. Nothing that is running changes."
        confirmLabel="Delete release"
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => remove.mutate()}
      />

      {/* Pre-seeded to pin *this* release, which is what makes the row action one click rather than a
          dialog the reader has to re-find their release in. The same component the Instances roster's
          bulk action opens; with a template it can write the fleet default, without one it pins the
          product's stacks individually. */}
      <SetReleaseDialog
        open={settingRelease}
        onOpenChange={setSettingRelease}
        productId={product.id}
        seedReleaseId={release.id}
        fleet={fleet}
        targets={targets}
      />
    </li>
  )
}

/**
 * The rollout summary on the expanded row: how far this release actually got, and the one action that
 * follows from a failure.
 *
 * Fetched only when the row is expanded — a 20-row page must not make 20 requests to render counts most
 * readers will not look at.
 */
function ReleaseRolloutSummary({ release }: { release: Release }) {
  const qc = useQueryClient()
  const { data, isLoading } = useQuery({
    queryKey: ['release', release.id, 'rollout'],
    queryFn: () => api.products.getReleaseRollout(release.id),
  })

  const retry = useMutation({
    mutationFn: () => api.products.retryFailedRollout(release.id),
    onSuccess: (result) => {
      qc.invalidateQueries({ queryKey: ['release', release.id, 'rollout'] })
      qc.invalidateQueries({ queryKey: ['stacks'] })
      toast.info(
        `Retrying ${result.retried} instance${result.retried === 1 ? '' : 's'}…`,
        // Two things the reader needs and would otherwise have to infer. First, what the retry
        // actually deploys: the enqueue carries no release id (invariant 3), so a latest-tracking
        // instance resolves the *current* newest release, which may not be this row's. Second, why the
        // count can be lower than the failure count.
        [
          'Each one deploys its pin, or the newest release if it tracks latest.',
          result.skipped > 0
            ? `${result.skipped} skipped — stopped, or pinned to another release.`
            : null,
        ]
          .filter(Boolean)
          .join(' '),
      )
    },
    onError: (err: Error) => toast.error('Couldn’t retry', err.message),
  })

  if (isLoading || !data) return null

  const counts = [
    data.succeeded > 0 && `${data.succeeded} succeeded`,
    data.failed > 0 && `${data.failed} failed`,
    data.running > 0 && `${data.running} running`,
    data.queued > 0 && `${data.queued} queued`,
    data.skipped > 0 && `${data.skipped} not deployed`,
  ].filter(Boolean)

  return (
    <div className="flex flex-wrap items-center justify-between gap-3 px-3 py-2.5 text-[13px]">
      <span className="min-w-0 text-text-2">
        {counts.length > 0 ? counts.join(' · ') : 'Nothing has deployed this release.'}
        {/* Said before the click, not only in the toast after it: the retry is convergent, so a
            latest-tracking instance deploys whatever is newest now rather than this row's release
            (invariant 3). A reader who expects "retry this release" would otherwise be surprised by a
            fleet that came back on something newer. */}
        {data.failed > 0 && (
          <span className="block text-[12px] text-text-3">
            A retry deploys each instance’s pin, or the newest release if it tracks latest.
          </span>
        )}
      </span>
      {data.failed > 0 && (
        <Button
          variant="secondary"
          size="sm"
          loading={retry.isPending}
          onClick={() => retry.mutate()}
        >
          {!retry.isPending && <RotateCcw />}
          Retry failed instances
        </Button>
      )}
    </div>
  )
}

/** The expansion: what this build actually pins, one row per repository. */
function ReleaseImages({ releaseId }: { releaseId: number }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['release', releaseId],
    queryFn: () => api.products.getRelease(releaseId),
  })

  if (isLoading) {
    return (
      <div className="flex justify-center border-t border-border bg-surface-2 py-6">
        <Spinner />
      </div>
    )
  }
  if (isError || !data) {
    return (
      <p className="border-t border-border bg-surface-2 px-3 py-3 text-[13px] text-danger">
        {(error as Error)?.message ?? 'Couldn’t load this release.'}
      </p>
    )
  }

  return (
    <div className="space-y-3 border-t border-border bg-surface-2 px-3 py-3">
      <table className="w-full text-[13px]">
        <thead>
          <tr className="text-left text-[12px] text-text-3">
            <th className="pb-1 font-medium">Repository</th>
            <th className="pb-1 font-medium">Tag</th>
            <th className="pb-1 font-medium">Digest</th>
          </tr>
        </thead>
        <tbody>
          {data.images.map((image) => (
            <tr key={image.repository} className="align-middle">
              <td className="py-1 pr-3 font-mono text-text">{image.repository}</td>
              <td className="py-1 pr-3 font-mono text-text-2">{image.tag ?? '—'}</td>
              <td className="py-1">
                <span className="inline-flex items-center gap-1">
                  <span className="font-mono text-text-2" title={image.digest}>
                    {truncateMiddle(image.digest)}
                  </span>
                  <CopyButton value={image.digest} />
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {data.notes && <p className="whitespace-pre-wrap text-[13px] text-text-2">{data.notes}</p>}

      {data.sourceRunUrl && (
        <a
          href={data.sourceRunUrl}
          target="_blank"
          rel="noreferrer"
          className="inline-flex items-center gap-1 text-[13px] text-brand hover:underline"
        >
          View the CI run <ExternalLink className="size-3" aria-hidden />
        </a>
      )}
    </div>
  )
}

/**
 * The teaching card: the token a workflow presents, where to put it, and the exact call to make.
 *
 * Collapses to a link once releases exist — after the first one the reader knows how this works, and
 * a permanent setup card on a page they visit to read history is clutter (design.md §Übersichtlichkeit).
 */
function ReportFromCiCard({
  product,
  token,
  hasReleases,
  ready,
  ci,
}: {
  product: Product
  token: string | null
  hasReleases: boolean
  /** Whether the release list has actually answered — see the comment on the default below. */
  ready: boolean
  /** The CI link, or null when it could not be read — which reads as "no sync", i.e. the manual path. */
  ci: CiLink | null
}) {
  // Null until the reader says otherwise, so the default follows the data rather than whatever the
  // first render happened to see — the list is still loading then, and a state initialised from it
  // would leave the card expanded forever on a product that has releases.
  const [expanded, setExpanded] = useState<boolean | null>(null)

  // …and nothing at all until the list has answered: defaulting to open would flash a full setup card
  // on every product that has releases, defaulting to closed would flash a link on every one that
  // does not, and this card is never the reason someone opened the tab.
  if (!ready && expanded === null) return null

  const open = expanded ?? !hasReleases

  if (!open) {
    return (
      <div>
        <Button variant="link" onClick={() => setExpanded(true)}>
          Report a release from CI
        </Button>
      </div>
    )
  }

  return <ReportFromCiPanel product={product} token={token} ci={ci} />
}

/**
 * Whether Watchtower is already putting this product's token and URL into the repository — the one
 * question that decides which half of the card is shown. Both conditions are needed: the switch being
 * on is an intention, and only a completed push means a workflow referencing `vars.WATCHTOWER_URL`
 * would actually resolve to something. Pending or failed keeps the manual instructions, which is the
 * conservative direction — a reader who pastes a secret that was about to arrive anyway loses
 * nothing; a reader shown a snippet whose variables do not exist yet gets a failing job.
 */
function isSecretSyncLive(ci: CiLink | null): boolean {
  return ci?.syncReleaseSecrets === true && ci.releaseSecretsSync?.status === 'synced'
}

function ReportFromCiPanel({
  product,
  token,
  ci,
}: {
  product: Product
  token: string | null
  ci: CiLink | null
}) {
  const qc = useQueryClient()
  const [showWhat, setShowWhat] = useState(false)
  const synced = isSecretSyncLive(ci)
  // The "vice versa" cross-link: offered only where turning the sync on is actually possible, so a
  // non-GitHub remote or an install with no CI is never pointed at a tab that would tell it no.
  const syncAvailable = ci !== null && !ci.syncReleaseSecrets && ci.releaseSecretsSyncBlocked == null

  const invalidate = () => qc.invalidateQueries({ queryKey: ['product', product.id] })

  const rotate = useMutation({
    mutationFn: () => api.products.rotateReleaseToken(product.id),
    onSuccess: (result) => {
      invalidate()
      qc.invalidateQueries({ queryKey: ['product', product.id, 'ci'] })
      toast.success(
        'New release token generated.',
        result.resyncing
          ? 'It is being pushed to the repository’s Actions secrets — no workflow change needed.'
          : 'Update it wherever the old one was stored.',
      )
    },
    onError: (err: Error) => toast.error('Couldn’t rotate the token', err.message),
  })

  const toggle = useMutation({
    mutationFn: (enabled: boolean) => api.products.setReleaseWebhook(product.id, enabled),
    onSuccess: (state) => {
      invalidate()
      toast.success(state.enabled ? 'Release webhook enabled.' : 'Release webhook disabled.')
    },
    onError: (err: Error) => toast.error('Couldn’t change the webhook', err.message),
  })

  const webhookUrl = `${window.location.origin}/api/webhooks/products/${product.id}/release`

  return (
    <Card>
      <CardContent className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-sm font-medium text-text">Report a release from CI</span>
          <label className="flex items-center gap-2">
            <Switch
              checked={product.releaseWebhookEnabled}
              disabled={toggle.isPending}
              onCheckedChange={(v) => toggle.mutate(v)}
            />
            <span className="text-sm text-text">
              {product.releaseWebhookEnabled ? 'Enabled' : 'Disabled'}
            </span>
          </label>
        </div>

        {/* The definition is taught by the empty state above, which is on screen whenever this card
            is expanded by default; repeating it here would say the same sentence twice on one page. */}
        <p className="text-[13px] text-text-2">
          Your workflow posts one call after it pushes its images. Watchtower resolves the tags to
          digests and records the build — it does not deploy anything.
        </p>

        <Field
          label="Release token"
          hint="Presented as a bearer token by the workflow. Rotating it invalidates the previous one immediately."
        >
          <div className="flex flex-wrap items-center gap-2">
            <SecretField
              readOnly
              value={token ?? ''}
              placeholder="No token yet — generate one"
              aria-label="Release webhook token"
              className="min-w-0 flex-1"
            />
            <Button
              variant="secondary"
              size="sm"
              loading={rotate.isPending}
              onClick={() => rotate.mutate()}
            >
              {!rotate.isPending && <RotateCw />}
              {token ? 'Rotate' : 'Generate'}
            </Button>
          </div>
        </Field>

        {/* Two mutually exclusive halves. Synced: Watchtower already put the token, the URL and the
            product id in the repository, so there is nothing to place by hand and the snippet reads
            them as Actions variables. Not synced: the manual path, spelled out exactly as it always
            was — a hobby user without an admin PAT must never hit a wall here
            (docs/products/design.md §"Secret sync"). */}
        {synced ? (
          <p className="text-[13px] text-text-2">
            Synced automatically — this token and the two{' '}
            <code className="font-mono text-[12px]">WATCHTOWER_*</code> variables are kept in{' '}
            <span className="font-mono">{ci?.repo?.fullName ?? 'the repository'}</span>&rsquo;s
            Actions configuration, and re-pushed when you rotate it. Nothing to paste; see the{' '}
            <strong className="font-medium text-text">CI</strong> tab for the sync&rsquo;s state.
          </p>
        ) : (
          <>
            <p className="text-[13px] text-text-2">
              Add it to the repository: <strong className="font-medium text-text">Settings →
              Secrets and variables → Actions → New repository secret</strong>, name{' '}
              <code className="font-mono text-[12px]">WATCHTOWER_RELEASE_TOKEN</code>.
            </p>
            {syncAvailable && (
              <p className="text-[12px] text-text-3">
                Watchtower can place it for you instead — turn on release secret sync on the{' '}
                <strong className="font-medium text-text-2">CI</strong> tab.
              </p>
            )}
          </>
        )}

        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">
            <span className="text-[12px] text-text-3">
              {synced
                ? 'Add this step after the images are pushed:'
                : 'Then add this step after the images are pushed:'}
            </span>
            <CopyButton value={workflowSnippet(product, webhookUrl, synced)} label="Copy" />
          </div>
          <pre className="overflow-x-auto rounded-md bg-surface-2 px-2.5 py-1.5 font-mono text-[12px] text-text">
            {workflowSnippet(product, webhookUrl, synced)}
          </pre>
        </div>

        <div>
          <Button variant="link" onClick={() => setShowWhat((v) => !v)}>
            What this sends
          </Button>
          {showWhat && (
            <ul className="mt-1 list-disc space-y-1 pl-5 text-[13px] text-text-2">
              <li>
                The commit SHA the workflow built — no repository contents, and Watchtower does not
                clone anything to accept it.
              </li>
              <li>
                The branch, which must be the product’s{' '}
                <code className="font-mono text-[12px]">{product.defaultBranch}</code>: a build from
                anywhere else is refused, so a pull-request run cannot publish a release.
              </li>
              <li>
                The image references it pushed. Watchtower resolves each tag to its manifest digest
                itself, so the release pins exactly the build that ran.
              </li>
            </ul>
          )}
        </div>
      </CardContent>
    </Card>
  )
}

/**
 * The workflow step, with this product's id, branch and URL already substituted — the point of the
 * card (design.md: "the real win is the pre-filled snippet on the product page").
 *
 * Two forms, and which one is right is a fact about the repository rather than a preference.
 * `${'$'}{{ vars.WATCHTOWER_URL }}` and `${'$'}{{ vars.WATCHTOWER_PRODUCT_ID }}` are the design's own
 * workflow step and the form to prefer — but only once secret sync has actually pushed those
 * variables. Until then a snippet naming them would post to an empty URL and fail with nothing to
 * point at, so the literal URL is substituted instead. The token is `secrets.WATCHTOWER_RELEASE_TOKEN`
 * either way: synced or pasted, that is where a workflow reads it from.
 */
function workflowSnippet(product: Product, webhookUrl: string, synced: boolean): string {
  const image = suggestedImage(product)
  const url = synced
    ? '${{ vars.WATCHTOWER_URL }}/api/webhooks/products/${{ vars.WATCHTOWER_PRODUCT_ID }}/release'
    : webhookUrl
  return [
    '- name: Report release to Watchtower',
    `  if: github.ref == 'refs/heads/${product.defaultBranch}'`,
    '  run: |',
    '    curl -sSf -X POST \\',
    `      "${url}" \\`,
    '      -H "Authorization: Bearer \${{ secrets.WATCHTOWER_RELEASE_TOKEN }}" \\',
    '      -H "Content-Type: application/json" \\',
    '      -d @- <<JSON',
    '    {"commit":"\${{ github.sha }}","branch":"\${{ github.ref_name }}",',
    `     "images":["${image}:\${{ github.sha }}"],`,
    '     "runUrl":"\${{ github.server_url }}/\${{ github.repository }}/actions/runs/\${{ github.run_id }}"}',
    '    JSON',
  ].join('\n')
}

/** A plausible image name for the snippet: the repository's own path on ghcr, else a placeholder. */
function suggestedImage(product: Product): string {
  const match = /github\.com[/:]([^/]+)\/([^/]+?)(?:\.git)?\/?$/i.exec(product.repositoryUrl.trim())
  const [, owner, name] = match ?? []
  return owner && name ? `ghcr.io/${owner.toLowerCase()}/${name.toLowerCase()}` : 'ghcr.io/OWNER/IMAGE'
}

/**
 * Recording a build by hand — for adopting releases before the workflow is wired, and for the
 * remotes that have no CI to wire. Runs the same intake as the webhook, so the errors it surfaces
 * are the same ones a workflow would get; they are shown verbatim.
 */
function RecordReleaseDialog({
  product,
  open,
  onOpenChange,
}: {
  product: Product
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const qc = useQueryClient()
  const [version, setVersion] = useState('')
  const [commitSha, setCommitSha] = useState('')
  const [images, setImages] = useState('')
  const [notes, setNotes] = useState('')
  const [error, setError] = useState<string | null>(null)

  const reset = () => {
    setVersion('')
    setCommitSha('')
    setImages('')
    setNotes('')
    setError(null)
  }

  const create = useMutation({
    mutationFn: () =>
      api.products.createRelease(product.id, {
        version: version.trim(),
        commitSha: commitSha.trim() || null,
        images: images
          .split('\n')
          .map((line) => line.trim())
          .filter((line) => line.length > 0),
        notes: notes.trim() || null,
      }),
    onSuccess: (release) => {
      qc.invalidateQueries({ queryKey: ['product', product.id] })
      toast.success(`Release ${release.version} recorded.`)
      reset()
      onOpenChange(false)
    },
    // The server's message names the image, the branch or the version — say it as it was said.
    onError: (err: Error) => setError(err.message),
  })

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) reset()
        onOpenChange(next)
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Record a release</DialogTitle>
          <DialogDescription>
            For a build that already exists. Watchtower resolves each tag to its digest, so the
            release pins the same images a deploy would pull right now.
          </DialogDescription>
        </DialogHeader>

        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            create.mutate()
          }}
        >
          <Field label="Version" required hint="Unique for this product, e.g. 1.4.0.">
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                value={version}
                onChange={(e) => setVersion(e.target.value)}
                placeholder="1.4.0"
                required
              />
            )}
          </Field>

          <Field label="Commit" hint="The full 40-character SHA this build came from. Optional.">
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                value={commitSha}
                onChange={(e) => setCommitSha(e.target.value)}
                placeholder="a1b2c3d4e5f6…"
                className="font-mono"
              />
            )}
          </Field>

          <Field
            label="Images"
            required
            hint="One per line: repo:tag or repo@sha256:… — at most 20."
          >
            {({ id, describedBy }) => (
              <textarea
                id={id}
                aria-describedby={describedBy}
                value={images}
                onChange={(e) => setImages(e.target.value)}
                rows={4}
                spellCheck={false}
                required
                placeholder={`${suggestedImage(product)}:1.4.0`}
                className="w-full rounded-md border border-border-strong bg-surface-2 px-3 py-2 font-mono text-[13px] text-text outline-none placeholder:text-text-3 focus-visible:shadow-[var(--sh-focus)]"
              />
            )}
          </Field>

          <Field label="Notes" hint="Shown when the release is expanded. Optional.">
            {({ id, describedBy }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                value={notes}
                onChange={(e) => setNotes(e.target.value)}
                placeholder="What changed"
              />
            )}
          </Field>

          {error && (
            <Banner tone="danger" title="Couldn’t record this release">
              {error}
            </Banner>
          )}

          <DialogFooter>
            <Button type="button" variant="secondary" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={create.isPending}>
              Record release
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
