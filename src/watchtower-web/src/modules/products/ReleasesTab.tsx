import { useState } from 'react'
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, ExternalLink, Package, Plus, RotateCw, Trash2 } from 'lucide-react'
import { api } from '@/lib/api'
import type { Product, Release } from '@/lib/types'
import { absoluteTitle, timeAgo, truncateMiddle } from '@/lib/format'
import { commitUrl } from '@/lib/source'
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

  const rows = releases.data?.pages.flatMap((p) => p.releases) ?? []
  const newestId = rows[0]?.id

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
        ready={releases.isSuccess}
      />

      <RecordReleaseDialog product={product} open={recording} onOpenChange={setRecording} />
    </div>
  )
}

/** One release: the row everything is read off, expanding to the images it pins. */
function ReleaseRow({
  product,
  release,
  isLatest,
}: {
  product: Product
  release: Release
  isLatest: boolean
}) {
  const qc = useQueryClient()
  const [expanded, setExpanded] = useState(false)
  const [confirming, setConfirming] = useState(false)

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

      {expanded && <ReleaseImages releaseId={release.id} />}

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
    </li>
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
}: {
  product: Product
  token: string | null
  hasReleases: boolean
  /** Whether the release list has actually answered — see the comment on the default below. */
  ready: boolean
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

  return <ReportFromCiPanel product={product} token={token} />
}

function ReportFromCiPanel({ product, token }: { product: Product; token: string | null }) {
  const qc = useQueryClient()
  const [showWhat, setShowWhat] = useState(false)

  const invalidate = () => qc.invalidateQueries({ queryKey: ['product', product.id] })

  const rotate = useMutation({
    mutationFn: () => api.products.rotateReleaseToken(product.id),
    onSuccess: () => {
      invalidate()
      toast.success('New release token generated.', 'Update it wherever the old one was stored.')
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

        {/* Secret sync arrives with stage 5; until then the operator pastes it, so the path is spelled
            out rather than implied. */}
        <p className="text-[13px] text-text-2">
          Add it to the repository: <strong className="font-medium text-text">Settings →
          Secrets and variables → Actions → New repository secret</strong>, name{' '}
          <code className="font-mono text-[12px]">WATCHTOWER_RELEASE_TOKEN</code>.
        </p>

        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">
            <span className="text-[12px] text-text-3">
              Then add this step after the images are pushed:
            </span>
            <CopyButton value={workflowSnippet(product, webhookUrl)} label="Copy" />
          </div>
          <pre className="overflow-x-auto rounded-md bg-surface-2 px-2.5 py-1.5 font-mono text-[12px] text-text">
            {workflowSnippet(product, webhookUrl)}
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
 * `WATCHTOWER_URL` and `WATCHTOWER_PRODUCT_ID` are deliberately *not* referenced as Actions variables:
 * those arrive with the secret-sync stage, and a snippet naming variables nobody has set would fail
 * with an empty URL. The token is the one value the operator has to place by hand.
 */
function workflowSnippet(product: Product, webhookUrl: string): string {
  const image = suggestedImage(product)
  return [
    '- name: Report release to Watchtower',
    `  if: github.ref == 'refs/heads/${product.defaultBranch}'`,
    '  run: |',
    '    curl -sSf -X POST \\',
    `      "${webhookUrl}" \\`,
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
