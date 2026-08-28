import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Boxes, Flame, Github, Hammer, KeyRound, Play, RefreshCcw, RotateCcw } from 'lucide-react'
import { api } from '@/lib/api'
import { absoluteTitle, formatUptime, timeAgo } from '@/lib/format'
import type {
  CiLink,
  CiRegistrySync,
  CiReleaseSecretsSync,
  CiRepo,
  CiRunnerContainer,
  CiToolchainProfile,
  Product,
} from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
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
import { Switch } from '@/components/ui/switch'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'

// ── CI tab — per-product GitHub Actions runners (docs/ci-runners/design.md) ──────
//
// CI belongs to the repository, so it lives on the product (ADR-0026): one runner pool and one
// toolcache shared by every stack deploying it, configured in one place instead of on whichever
// instance the operator happened to open.

const toolchainLabels: Record<string, string> = {
  dotnet: '.NET',
  node: 'Node',
  go: 'Go',
}

/** "detected: .NET 10.0 · Node 22 · Dockerfile" chips. */
function ToolchainChips({ profile }: { profile: CiToolchainProfile }) {
  if (profile.toolchains.length === 0 && !profile.hasDockerfile) {
    return <span className="text-[13px] text-text-3">No known toolchains detected.</span>
  }
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {profile.toolchains.map((t) => (
        <Badge key={`${t.kind}-${t.version}`} tone="neutral" size="sm" title={`from ${t.source}`}>
          {toolchainLabels[t.kind] ?? t.kind} {t.version}
        </Badge>
      ))}
      {profile.hasDockerfile && (
        <Badge tone="neutral" size="sm" title="The repository contains a Dockerfile">
          Dockerfile
        </Badge>
      )}
    </div>
  )
}

/** Warm state of the toolcache volume relative to the detected profile. */
function WarmBadge({ profile }: { profile: CiToolchainProfile }) {
  switch (profile.warmStatus) {
    case 'warmed':
      return <Badge tone="ok" size="sm">cache warm</Badge>
    case 'warming':
      return <Badge tone="run" size="sm">warming…</Badge>
    case 'failed':
      return <Badge tone="danger" size="sm">warm failed</Badge>
    default:
      return <Badge tone="neutral" size="sm">warm pending</Badge>
  }
}

/** One runner container as a card, for the DataList's <768px layout. */
function RunnerCard({
  repo,
  runner,
  onRecycle,
  recycling,
}: {
  repo: CiRepo
  runner: CiRunnerContainer
  onRecycle: (runner: CiRunnerContainer) => void
  recycling: boolean
}) {
  return (
    <div className="space-y-2">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="truncate font-mono text-[13px] text-text">{runner.name}</div>
          <div className="font-mono text-[11px] text-text-3">{runner.id}</div>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <RunnerStateBadges runner={runner} />
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`Recycle ${runner.name}`}
            loading={recycling}
            onClick={() => onRecycle(runner)}
          >
            {!recycling && <RotateCcw />}
          </Button>
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-text-2">
        <span className="tnum" title={absoluteTitle(runner.startedAt)}>
          {runner.startedAt ? `up ${formatUptime(runner.startedAt)}` : runner.status}
        </span>
        <span className="truncate font-mono">{runner.image}</span>
        <RunnerGitHubLink repo={repo} runner={runner} />
      </div>
    </div>
  )
}

function RunnerStateBadges({ runner }: { runner: CiRunnerContainer }) {
  return (
    <div className="flex shrink-0 flex-wrap items-center gap-1.5">
      <Badge tone={runner.state === 'running' ? 'ok' : 'run'} size="sm">
        {runner.state}
      </Badge>
      {runner.stale && (
        <Badge
          tone="warn"
          size="sm"
          title="Spawned before the current runner settings were saved. It keeps the job it is running and is replaced once idle."
        >
          settings changed
        </Badge>
      )}
    </div>
  )
}

/** The runner as GitHub knows it — the same id the repository's runner settings list shows. */
function RunnerGitHubLink({ repo, runner }: { repo: CiRepo; runner: CiRunnerContainer }) {
  if (runner.gitHubRunnerId == null) return <span className="text-text-3">—</span>
  return (
    <a
      href={`https://github.com/${repo.fullName}/settings/actions/runners/${runner.gitHubRunnerId}`}
      target="_blank"
      rel="noreferrer"
      className="tnum text-brand hover:underline"
      title={`Runner #${runner.gitHubRunnerId} in ${repo.fullName}'s Actions settings`}
    >
      #{runner.gitHubRunnerId}
    </a>
  )
}

/**
 * The live runner containers behind the slot count. Watchtower keeps no runner table in the
 * database — the containers *are* the state (docs/ci-runners/design.md) — so this is the only place
 * they are visible outside `docker ps` on the host. The list is the orchestrator's last reconcile
 * pass, which is why a runner spawned seconds ago can trail the count above it by one interval.
 */
function RunnerContainersCard({ productId, repo }: { productId: number; repo: CiRepo }) {
  const qc = useQueryClient()
  const runners = repo.runnerStatus?.runners ?? []
  // The busy escalation: a non-forced recycle that GitHub refused parks the runner here until the
  // operator confirms killing the job it is executing (or dismisses the dialog).
  const [forceTarget, setForceTarget] = useState<CiRunnerContainer | null>(null)
  const [confirmForceAll, setConfirmForceAll] = useState(false)

  const invalidate = () => qc.invalidateQueries({ queryKey: ['product', productId, 'ci'] })

  const recycleOne = useMutation({
    mutationFn: ({ runner, force }: { runner: CiRunnerContainer; force: boolean }) =>
      api.ci.recycleRunner(repo.id, runner.id, force),
    onSuccess: (res, { runner }) => {
      if (res.recycled) {
        setForceTarget(null)
        invalidate()
        toast.success(`Recycled ${runner.name} — a fresh runner takes its place.`)
      } else if (res.busy) {
        setForceTarget(runner)
      }
    },
    onError: (err: Error) => {
      setForceTarget(null)
      toast.error('Recycle failed', err.message)
    },
  })

  const recycleAll = useMutation({
    mutationFn: (force: boolean) => api.ci.recycleRunners(repo.id, force),
    onSuccess: (res, force) => {
      invalidate()
      if (res.recycled > 0) {
        toast.success(`Recycled ${res.recycled} runner(s) — fresh ones take their place.`)
      }
      if (res.busy > 0 && !force) {
        setConfirmForceAll(true)
      } else {
        setConfirmForceAll(false)
        if (res.recycled === 0 && res.busy === 0) {
          toast.success('No runner containers to recycle.')
        }
      }
    },
    onError: (err: Error) => {
      setConfirmForceAll(false)
      toast.error('Recycle failed', err.message)
    },
  })

  const recyclingId = recycleOne.isPending ? recycleOne.variables?.runner.id : null

  const columns: DataListColumn<CiRunnerContainer>[] = [
    {
      key: 'container',
      header: 'Container',
      cell: (r) => (
        <div className="min-w-0">
          <div className="truncate font-mono text-[13px] text-text">{r.name}</div>
          <div className="font-mono text-[11px] text-text-3">{r.id}</div>
        </div>
      ),
    },
    {
      key: 'state',
      header: 'State',
      cell: (r) => <RunnerStateBadges runner={r} />,
    },
    {
      key: 'uptime',
      header: 'Uptime',
      cell: (r) => (
        <span className="tnum text-[13px] text-text-2" title={absoluteTitle(r.startedAt)}>
          {r.startedAt ? formatUptime(r.startedAt) : r.status}
        </span>
      ),
    },
    {
      key: 'image',
      header: 'Image',
      cell: (r) => <span className="font-mono text-[12px] text-text-2">{r.image}</span>,
    },
    {
      key: 'github',
      header: 'GitHub',
      align: 'right',
      cell: (r) => (
        <span className="text-[12px]">
          <RunnerGitHubLink repo={repo} runner={r} />
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (r) => (
        <Tooltip label="Recycle — recreate under the current settings">
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`Recycle ${r.name}`}
            loading={recyclingId === r.id}
            onClick={() => recycleOne.mutate({ runner: r, force: false })}
          >
            {recyclingId !== r.id && <RotateCcw />}
          </Button>
        </Tooltip>
      ),
    },
  ]

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
            <span className="text-sm text-text">Runner containers</span>
            <span className="text-[12px] text-text-3">
              Ephemeral — each takes one job, exits, and is replaced.
            </span>
          </div>
          {runners.length > 0 && (
            <Button
              variant="secondary"
              size="sm"
              loading={recycleAll.isPending}
              onClick={() => recycleAll.mutate(false)}
            >
              {!recycleAll.isPending && <RefreshCcw />}
              Recycle all
            </Button>
          )}
        </div>
        <DataList
          items={runners}
          getKey={(r) => r.id}
          columns={columns}
          renderCard={(r) => (
            <RunnerCard
              repo={repo}
              runner={r}
              onRecycle={(runner) => recycleOne.mutate({ runner, force: false })}
              recycling={recyclingId === r.id}
            />
          )}
          emptyState={
            <p className="text-[13px] text-text-3">
              No runner containers on this host yet — the next reconcile pass starts them.
            </p>
          }
          aria-label="Runner containers"
        />

        <ConfirmDialog
          open={forceTarget != null}
          onOpenChange={(open) => !open && setForceTarget(null)}
          title={`${forceTarget?.name ?? 'Runner'} is executing a job`}
          description="GitHub won't release a runner mid-job. Kill the container anyway? The running job fails, and a fresh runner takes the slot."
          confirmLabel="Kill and recycle"
          tone="danger"
          loading={recycleOne.isPending}
          onConfirm={() => forceTarget && recycleOne.mutate({ runner: forceTarget, force: true })}
        />
        <ConfirmDialog
          open={confirmForceAll}
          onOpenChange={setConfirmForceAll}
          title="Some runners are executing jobs"
          description="The idle runners were recycled; the busy ones were kept. Kill the remaining containers anyway? Their running jobs fail, and fresh runners take the slots."
          confirmLabel="Kill and recycle"
          tone="danger"
          loading={recycleAll.isPending}
          onConfirm={() => recycleAll.mutate(true)}
        />
      </CardContent>
    </Card>
  )
}

export function ProductCiTab({ product }: { product: Product }) {
  const {
    data: ci,
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['product', product.id, 'ci'],
    queryFn: () => api.ci.getProductCi(product.id),
    // Runner slots and warm state are live orchestrator data — poll while CI is enabled. Also poll
    // while a release-secret push is pending, regardless of `enabled`: that contributor runs
    // independently of it by design, so a disabled repo still converges and the badge still moves.
    refetchInterval: (q) =>
      q.state.data?.repo?.enabled || q.state.data?.releaseSecretsSync?.status === 'pending'
        ? 10_000
        : false,
  })

  if (isError) {
    return (
      <Banner
        tone="danger"
        title="Couldn’t load CI state"
        action={
          <Button variant="secondary" size="sm" onClick={() => refetch()}>
            Retry
          </Button>
        }
      />
    )
  }
  if (isLoading || !ci) {
    return <Skeleton className="h-40" />
  }

  if (!ci.isGitHub) {
    return (
      <EmptyState
        icon={Github}
        title="CI runners need a GitHub repository"
        description={`This product deploys from ${product.repositoryUrl}, which is not a github.com repository. Watchtower-managed runners register with GitHub Actions, so only GitHub repositories can use them.`}
      />
    )
  }

  return ci.repo ? (
    <CiRepoPanel product={product} ci={ci} repo={ci.repo} />
  ) : (
    <EnableCiCard product={product} owner={ci.owner!} name={ci.name!} />
  )
}

/** The "not enabled yet" card: explains what enabling does and probes the PAT up front. */
function EnableCiCard({ product, owner, name }: { product: Product; owner: string; name: string }) {
  const qc = useQueryClient()
  const [credentialId, setCredentialId] = useState<number | null>(product.credentialId)
  const [error, setError] = useState<string | null>(null)

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const enable = useMutation({
    mutationFn: () => api.ci.enableForProduct(product.id, credentialId),
    onSuccess: (repo) => {
      setError(null)
      qc.invalidateQueries({ queryKey: ['product', product.id, 'ci'] })
      toast.success(`CI runners enabled for ${repo.fullName}.`)
    },
    // The server names the exact missing PAT permission — show its message verbatim.
    onError: (err: Error) => setError(err.message),
  })

  return (
    <div className="space-y-4">
      <SectionHeader
        title="CI runners"
        description={`Run this repository’s GitHub Actions jobs on this box in ephemeral containers.`}
      />
      <Card>
        <CardContent className="space-y-4">
          <p className="text-[13px] text-text-2">
            Enabling CI registers ephemeral, just-in-time runners for{' '}
            <span className="font-mono text-text">{owner}/{name}</span> — no tokens are copied into
            containers, each runner takes one job and exits, and per-repo caches keep builds fast.
            Products deploying the same repository share one runner pool.
          </p>

          <Field
            label="Credential"
            hint="Needs a fine-grained PAT with repository Administration (read and write) — more than cloning needs."
          >
            <Select
              value={credentialId != null ? String(credentialId) : ''}
              onValueChange={(v) => setCredentialId(Number(v))}
            >
              <SelectTrigger>
                <SelectValue placeholder="Choose a credential" />
              </SelectTrigger>
              <SelectContent>
                {credentials.map((c) => (
                  <SelectItem key={c.id} value={String(c.id)}>
                    {c.name} ({c.username})
                    {c.id === product.credentialId ? ' — used for cloning' : ''}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>

          {error && (
            <Banner tone="danger" title="Couldn’t enable CI">
              {error}
            </Banner>
          )}

          <Button
            loading={enable.isPending}
            disabled={credentialId == null}
            onClick={() => enable.mutate()}
          >
            {!enable.isPending && <Play />}
            Enable CI
          </Button>
        </CardContent>
      </Card>
    </div>
  )
}

/** The enabled view: runner slots, detected toolchains + cache warmth, and runner settings. */
function CiRepoPanel({ product, ci, repo }: { product: Product; ci: CiLink; repo: CiRepo }) {
  const qc = useQueryClient()
  const status = repo.runnerStatus
  const [maxRunners, setMaxRunners] = useState(repo.maxConcurrentRunners)

  const update = useMutation({
    mutationFn: (
      changes: Partial<
        Pick<CiRepo, 'enabled' | 'maxConcurrentRunners' | 'allowDockerSocket' | 'syncRegistryUrl'>
      >,
    ) =>
      api.ci.updateRepo({
        id: repo.id,
        enabled: changes.enabled ?? repo.enabled,
        maxConcurrentRunners: changes.maxConcurrentRunners ?? repo.maxConcurrentRunners,
        credentialId: repo.credentialId,
        runnerImage: repo.runnerImage,
        extraLabels: repo.extraLabels,
        allowDockerSocket: changes.allowDockerSocket ?? repo.allowDockerSocket,
        syncRegistryUrl:
          changes.syncRegistryUrl !== undefined ? changes.syncRegistryUrl : repo.syncRegistryUrl,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['product', product.id, 'ci'] }),
    onError: (err: Error) => toast.error('Update failed', err.message),
  })

  return (
    <div className="space-y-4">
      <SectionHeader
        title="CI runners"
        description={`GitHub Actions jobs for ${repo.fullName} run here in ephemeral containers.`}
        action={
          <label className="flex items-center gap-2">
            <Switch
              checked={repo.enabled}
              onCheckedChange={(v) => update.mutate({ enabled: v })}
            />
            <span className="text-sm text-text">{repo.enabled ? 'Enabled' : 'Disabled'}</span>
          </label>
        }
      />
      <p className="-mt-2 text-[13px] text-text-2">
        Shared by every stack deploying{' '}
        <span className="font-mono">{repo.fullName}</span> — one runner pool, one cache.
      </p>

      {status?.lastError && (
        <Banner tone="danger" title="Runner orchestration is failing">
          {status.lastError}
          {status.backoffUntil &&
            ` Retrying after ${new Date(status.backoffUntil).toLocaleTimeString()}.`}
        </Banner>
      )}

      {/* Runner slots */}
      <Card>
        <CardContent className="space-y-4">
          <div className="flex flex-wrap items-center gap-3">
            <Hammer className="size-4 text-text-2" aria-hidden />
            <span className="text-sm text-text">
              {repo.enabled
                ? `${status?.runningRunners ?? 0} of ${status?.desiredRunners ?? repo.maxConcurrentRunners} runner slot(s) live`
                : 'Runners are disabled.'}
            </span>
            {status != null && status.totalSpawned > 0 && (
              <span className="tnum text-[12px] text-text-3">
                {status.totalSpawned} runner(s) spawned since start
              </span>
            )}
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Max concurrent runners" hint="Runner slots kept registered for this repository.">
              <div className="flex items-center gap-2">
                <Input
                  type="number"
                  min={1}
                  max={16}
                  value={maxRunners}
                  onChange={(e) => setMaxRunners(Number(e.target.value))}
                  className="w-24"
                />
                {maxRunners !== repo.maxConcurrentRunners && (
                  <Button
                    size="sm"
                    variant="secondary"
                    loading={update.isPending}
                    onClick={() => update.mutate({ maxConcurrentRunners: maxRunners })}
                  >
                    Apply
                  </Button>
                )}
              </div>
            </Field>

            <Field
              label="Docker socket"
              hint="Host-root equivalent. Only for trusted repos whose jobs build or push images."
            >
              <label className="flex items-center gap-3 pt-1.5">
                <Switch
                  checked={repo.allowDockerSocket}
                  onCheckedChange={(v) => update.mutate({ allowDockerSocket: v })}
                />
                <span className="text-sm text-text">Mount /var/run/docker.sock into runners</span>
              </label>
            </Field>
          </div>
        </CardContent>
      </Card>

      {/* The containers behind the slot count. Also rendered while CI is off if any are still
          being torn down, so "disabled" does not look like "already gone". */}
      {(repo.enabled || (repo.runnerStatus?.runners.length ?? 0) > 0) && (
        <RunnerContainersCard productId={product.id} repo={repo} />
      )}

      {/* Registry sync (docs/ci-runners/design.md, Secrets §1) */}
      <RegistrySyncCard repo={repo} onSelect={(url) => update.mutate({ syncRegistryUrl: url })} />

      {/* Release secret sync (docs/products/design.md §"Secret sync") — the second, independent
          contributor to the same repository's Actions config. */}
      <ReleaseSecretsSyncCard product={product} ci={ci} repo={repo} />

      {/* Toolchains + cache warming */}
      <Card>
        <CardContent className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="flex items-center gap-2">
              <Flame className="size-4 text-text-2" aria-hidden />
              <span className="text-sm font-medium text-text">Detected toolchains</span>
            </div>
            {repo.toolchain && <WarmBadge profile={repo.toolchain} />}
          </div>

          {repo.toolchain ? (
            <>
              <ToolchainChips profile={repo.toolchain} />
              <p className="text-[12px] text-text-3">
                Detected from the repository on{' '}
                {repo.toolchain.detectedAt
                  ? new Date(repo.toolchain.detectedAt).toLocaleString()
                  : 'the last deploy'}
                . Detected SDKs are pre-installed into this repo’s shared toolcache volume so
                setup-* actions skip their downloads.
              </p>
              {repo.toolchain.warmStatus === 'failed' && repo.toolchain.lastWarmError && (
                <Banner tone="warn" title="Toolcache warm-up failed (builds still run, just colder)">
                  <pre className="max-h-40 overflow-auto whitespace-pre-wrap font-mono text-[11px]">
                    {repo.toolchain.lastWarmError}
                  </pre>
                </Banner>
              )}
            </>
          ) : (
            <p className="text-[13px] text-text-2">
              Not detected yet — the toolchain profile is read from the repository during the next
              deploy of this product.
            </p>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

const NO_REGISTRY = 'none'

/** Badge for the registry -> GitHub Actions sync state. */
function RegistrySyncBadge({ sync }: { sync: CiRegistrySync }) {
  switch (sync.status) {
    case 'synced':
      return <Badge tone="ok" size="sm">synced</Badge>
    case 'failed':
      return <Badge tone="danger" size="sm">sync failed</Badge>
    default:
      return <Badge tone="neutral" size="sm">sync pending</Badge>
  }
}

const LOGIN_SNIPPET = `- uses: docker/login-action@v3
  with:
    registry: \${{ vars.REGISTRY }}
    username: \${{ secrets.REGISTRY_USERNAME }}
    password: \${{ secrets.REGISTRY_PASSWORD }}`

/**
 * Pick a registry (Watchtower-configured or from the host docker config) whose credentials are
 * pushed to the repo's GitHub Actions config: the REGISTRY variable plus the REGISTRY_USERNAME /
 * REGISTRY_PASSWORD secrets. Re-pushed automatically when the credential rotates.
 */
function RegistrySyncCard({
  repo,
  onSelect,
}: {
  repo: CiRepo
  onSelect: (url: string | null) => void
}) {
  const { data: registryData } = useQuery({
    queryKey: ['registries'],
    queryFn: api.registries.listWithHost,
  })
  const registries = registryData?.registries ?? []
  const hostRegistries = registryData?.hostRegistries ?? []

  // Watchtower registries first (they win on URL collision), then remaining host entries.
  const options = [
    ...registries
      .filter((r) => r.credentialId != null)
      .map((r) => ({ url: r.url, label: `${r.name} (${r.url})` })),
    ...hostRegistries
      .filter((h) => h.username != null)
      .map((h) => ({ url: h.url, label: `${h.url} — docker config` })),
  ]
  // A previously selected registry that no longer resolves still needs to render (and be clearable).
  if (repo.syncRegistryUrl && !options.some((o) => o.url === repo.syncRegistryUrl)) {
    options.push({ url: repo.syncRegistryUrl, label: `${repo.syncRegistryUrl} — no longer found` })
  }

  const sync = repo.registrySync

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex items-center gap-2">
            <Boxes className="size-4 text-text-2" aria-hidden />
            <span className="text-sm font-medium text-text">Registry for workflows</span>
          </div>
          {sync && <RegistrySyncBadge sync={sync} />}
        </div>

        <p className="text-[13px] text-text-2">
          Syncs the selected registry to the repository&rsquo;s GitHub Actions configuration as the{' '}
          <code className="font-mono text-[12px]">REGISTRY</code> variable plus the{' '}
          <code className="font-mono text-[12px]">REGISTRY_USERNAME</code> /{' '}
          <code className="font-mono text-[12px]">REGISTRY_PASSWORD</code> secrets, and re-syncs
          when the credential rotates. The PAT needs the repository Secrets and Variables (read and
          write) permissions.
        </p>

        <Field label="Registry" hint="Watchtower registries and host docker-config logins.">
          <Select
            value={repo.syncRegistryUrl ?? NO_REGISTRY}
            onValueChange={(v) => onSelect(v === NO_REGISTRY ? null : v)}
          >
            <SelectTrigger>
              <SelectValue placeholder="No registry sync" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={NO_REGISTRY}>None</SelectItem>
              {options.map((o) => (
                <SelectItem key={o.url} value={o.url}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>

        {sync?.status === 'failed' && sync.error && (
          <Banner tone="danger" title="Registry sync is failing (retried automatically)">
            {sync.error}
          </Banner>
        )}

        {repo.syncRegistryUrl && (
          <div className="space-y-1">
            <p className="text-[12px] text-text-3">
              Log in from a workflow with{' '}
              {sync?.syncedAt
                ? `the synced values (last synced ${new Date(sync.syncedAt).toLocaleString()}):`
                : 'the values once synced:'}
            </p>
            <pre className="overflow-x-auto rounded-md bg-surface-2 px-2.5 py-1.5 font-mono text-[12px] text-text">
              {LOGIN_SNIPPET}
            </pre>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ── Release secret sync (docs/products/design.md §"Secret sync") ─────────────────
//
// The second contributor to this repository's Actions config, and deliberately built to the same
// shape as RegistrySyncCard above: one enable control, one badge, one standing-failure banner
// quoting the server verbatim. What it pushes is this *product's* release configuration —
// WATCHTOWER_URL and WATCHTOWER_PRODUCT_ID as variables, WATCHTOWER_RELEASE_TOKEN as a sealed
// secret — so a workflow needs nothing pasted by hand.
//
// The token itself is not shown here. It lives on the Releases tab, which is where an operator goes
// to read it, and duplicating it would give the page two places to keep in step.

/** Badge for the release-secret sync state — the registry badge's vocabulary, verbatim. */
function ReleaseSyncBadge({ sync }: { sync: CiReleaseSecretsSync }) {
  switch (sync.status) {
    case 'synced':
      return <Badge tone="ok" size="sm">synced</Badge>
    case 'failed':
      return <Badge tone="danger" size="sm">sync failed</Badge>
    default:
      return <Badge tone="neutral" size="sm">sync pending</Badge>
  }
}

function ReleaseSecretsSyncCard({
  product,
  ci,
  repo,
}: {
  product: Product
  ci: CiLink
  repo: CiRepo
}) {
  const qc = useQueryClient()
  // The server's message is the whole value here — the monorepo conflict names the other product, the
  // PAT failure names the missing permission — so it is shown verbatim rather than summarised.
  const [error, setError] = useState<string | null>(null)

  const toggle = useMutation({
    mutationFn: (enabled: boolean) => api.ci.setReleaseSecretsSync(product.id, enabled),
    onSuccess: (link) => {
      setError(null)
      qc.setQueryData(['product', product.id, 'ci'], link)
      qc.invalidateQueries({ queryKey: ['product', product.id] })
      toast.success(
        link.syncReleaseSecrets
          ? 'Release secrets will be synced to GitHub Actions.'
          : 'Release secret sync turned off.',
      )
    },
    onError: (err: Error) => setError(err.message),
  })

  const sync = ci.releaseSecretsSync

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex items-center gap-2">
            <KeyRound className="size-4 text-text-2" aria-hidden />
            <span className="text-sm font-medium text-text">Release secrets for workflows</span>
          </div>
          {sync && <ReleaseSyncBadge sync={sync} />}
        </div>

        <p className="text-[13px] text-text-2">
          Pushes what this product&rsquo;s release step needs into{' '}
          <span className="font-mono">{repo.fullName}</span>&rsquo;s Actions configuration: the{' '}
          <code className="font-mono text-[12px]">WATCHTOWER_URL</code> and{' '}
          <code className="font-mono text-[12px]">WATCHTOWER_PRODUCT_ID</code> variables plus the{' '}
          <code className="font-mono text-[12px]">WATCHTOWER_RELEASE_TOKEN</code> secret, re-pushed
          whenever the token is rotated. The PAT needs the repository Secrets and Variables (read and
          write) permissions — the same ones the registry sync above uses.
        </p>

        <Field
          label="Sync release secrets"
          hint="One product per repository can own these names. Turning it off leaves the values already at GitHub in place."
        >
          <label className="flex items-center gap-3 pt-1.5">
            <Switch
              checked={ci.syncReleaseSecrets}
              disabled={toggle.isPending}
              onCheckedChange={(v) => toggle.mutate(v)}
            />
            <span className="text-sm text-text">
              {ci.syncReleaseSecrets ? 'Enabled' : 'Disabled'}
            </span>
          </label>
        </Field>

        {error && (
          <Banner tone="danger" title="Couldn’t change the release secret sync">
            {error}
          </Banner>
        )}

        {sync?.status === 'failed' && sync.error && (
          <Banner tone="danger" title="Release secret sync is failing (retried automatically)">
            {sync.error}
          </Banner>
        )}

        {ci.syncReleaseSecrets && (
          <p className="text-[12px] text-text-3">
            {sync?.syncedAt ? (
              <span title={absoluteTitle(sync.syncedAt)}>Last synced {timeAgo(sync.syncedAt)}.</span>
            ) : (
              'Not pushed yet — the next reconcile pass does it.'
            )}{' '}
            The token itself is on the product&rsquo;s Releases tab, with the workflow step that uses
            it.
          </p>
        )}
      </CardContent>
    </Card>
  )
}
