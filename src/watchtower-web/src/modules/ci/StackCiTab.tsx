import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Flame, Github, Hammer, Play } from 'lucide-react'
import { api } from '@/lib/api'
import type { CiRepo, CiToolchainProfile, Stack } from '@/lib/types'
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
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/use-toast'

// ── CI tab — per-stack GitHub Actions runners (docs/ci-runners/design.md) ────────

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

export function StackCiTab({ stack }: { stack: Stack }) {
  const {
    data: ci,
    isLoading,
    isError,
    refetch,
  } = useQuery({
    queryKey: ['stacks', stack.id, 'ci'],
    queryFn: () => api.ci.getStackCi(stack.id),
    // Runner slots and warm state are live orchestrator data — poll while CI is enabled.
    refetchInterval: (q) => (q.state.data?.repo?.enabled ? 10_000 : false),
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
        description={`This stack deploys from ${stack.repositoryUrl}, which is not a github.com repository. Watchtower-managed runners register with GitHub Actions, so only GitHub repositories can use them.`}
      />
    )
  }

  return ci.repo ? (
    <CiRepoPanel stack={stack} repo={ci.repo} />
  ) : (
    <EnableCiCard stack={stack} owner={ci.owner!} name={ci.name!} />
  )
}

/** The "not enabled yet" card: explains what enabling does and probes the PAT up front. */
function EnableCiCard({ stack, owner, name }: { stack: Stack; owner: string; name: string }) {
  const qc = useQueryClient()
  const [credentialId, setCredentialId] = useState<number | null>(stack.credentialId)
  const [error, setError] = useState<string | null>(null)

  const { data: credentials = [] } = useQuery({
    queryKey: ['credentials'],
    queryFn: api.credentials.list,
  })

  const enable = useMutation({
    mutationFn: () => api.ci.enableForStack(stack.id, credentialId),
    onSuccess: (repo) => {
      setError(null)
      qc.invalidateQueries({ queryKey: ['stacks', stack.id, 'ci'] })
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
            Stacks deploying the same repository share one runner pool.
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
                    {c.id === stack.credentialId ? ' — used for cloning' : ''}
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
function CiRepoPanel({ stack, repo }: { stack: Stack; repo: CiRepo }) {
  const qc = useQueryClient()
  const status = repo.runnerStatus
  const [maxRunners, setMaxRunners] = useState(repo.maxConcurrentRunners)

  const update = useMutation({
    mutationFn: (changes: Partial<Pick<CiRepo, 'enabled' | 'maxConcurrentRunners' | 'allowDockerSocket'>>) =>
      api.ci.updateRepo({
        id: repo.id,
        enabled: changes.enabled ?? repo.enabled,
        maxConcurrentRunners: changes.maxConcurrentRunners ?? repo.maxConcurrentRunners,
        credentialId: repo.credentialId,
        runnerImage: repo.runnerImage,
        extraLabels: repo.extraLabels,
        allowDockerSocket: changes.allowDockerSocket ?? repo.allowDockerSocket,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['stacks', stack.id, 'ci'] }),
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
              deploy of this stack.
            </p>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
