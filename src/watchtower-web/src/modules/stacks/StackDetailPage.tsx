import { useCallback, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link, useParams } from '@tanstack/react-router'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { ChevronRight, Play, Square } from 'lucide-react'
import { stackDetailTabs, type HistoryRowControls } from '@/platform/points'
import { api } from '@/lib/api'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/use-toast'
import { deployTargetVersion, usesReleases } from '@/lib/release'
import { StackVersionFragment, useProductReleases } from './StackVersion'

const routeApi = getRouteApi('/stacks/$id')

export function StackDetailPage() {
  const { id } = useParams({ from: '/stacks/$id' })
  const stackId = Number(id)
  const qc = useQueryClient()
  // Whether the product in the header meta line is somewhere the reader can actually land.
  const productsEnabled = routeApi.useRouteContext().caps.isModuleEnabled('Products')

  // Tabs are contributed via the stackDetailTabs extension point, already sorted by order.
  const tabs = useContributions(stackDetailTabs)

  // Tab state lives in the URL via ?tab= (F9). Default to the first contributed tab's
  // value ('overview'); navigate replace:true.
  const { tab } = routeApi.useSearch()
  const navigateTab = routeApi.useNavigate()
  const defaultTab = tabs[0]?.value ?? 'overview'
  const activeTab = tab ?? defaultTab
  const setTab = useCallback(
    (next: string) => {
      navigateTab({
        search: (prev) => ({ ...prev, tab: next === defaultTab ? undefined : next }),
        replace: true,
      })
    },
    [navigateTab, defaultTab],
  )

  // Ref registry: deploy-history rows (rendered inside the Overview tab) register a
  // focus/expand handler here so the "View log" action on the failure banner can scroll
  // to + expand the latest failed row. Exposed to tabs via the search context below.
  const historyControls = useRef(new Map<number, HistoryRowControls>())
  const registerHistoryRow = useCallback(
    (eventId: number, controls: HistoryRowControls) => {
      historyControls.current.set(eventId, controls)
      return () => {
        historyControls.current.delete(eventId)
      }
    },
    [],
  )

  const {
    data: stack,
    isLoading: stackLoading,
    isError: stackError,
    refetch: refetchStack,
  } = useQuery({
    queryKey: ['stacks', stackId],
    queryFn: () => api.stacks.get(stackId),
    refetchInterval: (q) => {
      const s = q.state.data?.lastDeployStatus
      return s === 'running' || s === 'queued' ? 3000 : false
    },
  })

  const isDeploying =
    stack?.lastDeployStatus === 'running' || stack?.lastDeployStatus === 'queued'

  // Events are polled here (3s while deploying) so the failure hero can locate the latest
  // failed event for "View log", while the Overview tab renders the history rows.
  const { data: events = [] } = useQuery({
    queryKey: ['stacks', stackId, 'events'],
    queryFn: () => api.stacks.events(stackId),
    refetchInterval: isDeploying ? 3000 : false,
  })

  // Only in Releases mode, and shared (one cache key) with the header fragment, the Version dialog
  // and the Overview panel — three readers, one request.
  const releaseQuery = useProductReleases(
    stack?.productId ?? 0,
    stack != null && usesReleases(stack),
  )

  const deploy = useMutation({
    mutationFn: () => api.stacks.deploy(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'events'] })
      toast.info(`Deploying ${stack?.name ?? 'stack'}…`)
    },
    onError: (err: Error) => toast.error('Deploy failed', err.message),
  })

  const isStopped = stack?.desiredState === 'stopped'

  const stop = useMutation({
    mutationFn: () => api.stacks.stop(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      toast.success(`Stopped ${stack?.name ?? 'stack'}. It stays stopped until you start it again.`)
    },
    onError: (err: Error) => toast.error('Stop failed', err.message),
  })

  const start = useMutation({
    mutationFn: () => api.stacks.start(stackId),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      if (data.started) toast.success(`Started ${stack?.name ?? 'stack'}.`)
      else toast.info('Stack is enabled again — deploy it to create its containers.')
    },
    onError: (err: Error) => toast.error('Start failed', err.message),
  })

  function viewFailedLog() {
    // Find the most recent failed event and expand + scroll to it.
    const failed = [...events].find((e) => e.status === 'failed')
    if (!failed) return
    const controls = historyControls.current.get(failed.id)
    controls?.expand()
    // Let the row render its panel before scrolling.
    requestAnimationFrame(() => controls?.scrollTo())
  }

  if (stackLoading) return <StackDetailSkeleton />

  if (stackError || !stack) {
    return (
      <div>
        <Banner
          tone="danger"
          title="Couldn’t load this stack"
          action={
            <Button variant="secondary" size="sm" onClick={() => refetchStack()}>
              Retry
            </Button>
          }
        >
          The stack may have been deleted, or the server is unreachable.
        </Banner>
      </div>
    )
  }

  // Null in Git mode, which is what keeps the FAB below the circle it has always been.
  const deployVersion = usesReleases(stack)
    ? deployTargetVersion(stack, releaseQuery.data?.releases)
    : null

  return (
    <div className="space-y-6 pb-24 md:pb-0">
      {/* Breadcrumb */}
      <nav aria-label="Breadcrumb" className="flex items-center gap-1 text-xs text-text-2">
        <Link
          to="/stacks"
          className="inline-flex items-center gap-1 rounded transition-colors hover:text-text focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]"
        >
          <ChevronRight className="size-3.5 rotate-180" aria-hidden />
          Stacks
        </Link>
        <span aria-hidden className="text-text-3">
          /
        </span>
        <span className="truncate font-medium text-text">{stack.name}</span>
      </nav>

      {/* Header */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <h1 className="truncate text-2xl font-semibold tracking-tight text-text">{stack.name}</h1>
          {/* The source is the product's now, so the repository fragment names it and links to
              where it is edited — the only change this page carries from ADR-0026. Without the
              Products module the name stays, plain: its page would redirect straight back out. */}
          <p className="mt-1 truncate font-mono text-[12.5px] text-text-2">
            {productsEnabled ? (
              <Link
                to="/products/$id"
                params={{ id: String(stack.productId) }}
                className="hover:text-brand"
                title={stack.repositoryUrl}
              >
                {stack.productName}
              </Link>
            ) : (
              <span title={stack.repositoryUrl}>{stack.productName}</span>
            )}{' '}
            {/* The header invariant (design.md §Stack detail): this fragment always states the
                version the Deploy button would apply — `main@a1b2c3d` in Git mode, the release and
                its chips in Releases mode, where it is also the way into the Version dialog. */}
            · <StackVersionFragment stack={stack} />{' '}
            · {stack.composeFilePath}
          </p>
        </div>
        {/* Desktop actions; mobile uses the FAB below. */}
        <div className="hidden items-center gap-2 md:flex">
          <Button
            variant="secondary"
            loading={stop.isPending || start.isPending}
            disabled={isDeploying || stop.isPending || start.isPending}
            onClick={() => (isStopped ? start.mutate() : stop.mutate())}
          >
            {isStopped ? (
              <>
                <Play /> Start
              </>
            ) : (
              <>
                <Square /> Stop
              </>
            )}
          </Button>
          <Button
            variant="primary"
            loading={deploy.isPending || isDeploying}
            disabled={deploy.isPending || isDeploying || isStopped}
            onClick={() => deploy.mutate()}
          >
            {!(deploy.isPending || isDeploying) && <Play />}
            Deploy
          </Button>
        </div>
      </div>

      {/* Status banner hero */}
      {isStopped ? (
        <Banner
          tone="info"
          title="This stack is stopped"
          action={
            <Button
              variant="secondary"
              size="sm"
              loading={start.isPending}
              onClick={() => start.mutate()}
            >
              Start
            </Button>
          }
        >
          Its containers are stopped and deploys are rejected until you start it again — including
          across Watchtower and host restarts.
        </Banner>
      ) : isDeploying ? (
        <Banner tone="info" title="Deployment in progress…">
          Watchtower is pulling images and (re)starting containers.
        </Banner>
      ) : stack.lastDeployStatus === 'success' ? (
        <Banner tone="ok" title="Last deploy succeeded" />
      ) : stack.lastDeployStatus === 'failed' ? (
        <Banner
          tone="danger"
          title="Last deploy failed"
          action={
            <Button variant="secondary" size="sm" onClick={viewFailedLog}>
              View log
            </Button>
          }
        />
      ) : null}

      {/* Tabs (state in ?tab=, F9) — driven by the stackDetailTabs extension point. Each tab receives
          the slot context declared by the point: the stack plus registerHistoryRow, which the Overview
          tab's deploy-history rows use to wire the "View log" hero to the right row. */}
      <Tabs value={activeTab} onValueChange={setTab}>
        <TabsList>
          {tabs.map((t) => (
            <TabsTrigger key={t.id} value={t.value}>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>

        {tabs.map((t) => (
          <TabsContent key={t.id} value={t.value}>
            <t.component stack={stack} registerHistoryRow={registerHistoryRow} />
          </TabsContent>
        ))}
      </Tabs>

      {/* Mobile FAB (52px, above the bottom tab bar): deploy normally, start when stopped. */}
      <div className="fixed bottom-bottombar right-4 z-20 mb-3 md:hidden">
        {isStopped ? (
          <Button
            variant="primary"
            aria-label="Start stack"
            loading={start.isPending}
            disabled={start.isPending}
            onClick={() => start.mutate()}
            className="size-[52px] rounded-full p-0 shadow-[var(--sh-lg)]"
          >
            {!start.isPending && <Play />}
          </Button>
        ) : (
          // The header invariant covers the FAB too, and a fixed icon-only button outlives the
          // header line it would have to be read next to. In Releases mode it therefore grows into a
          // pill carrying the version it would deploy; Git mode keeps the 52px circle it has always
          // been (its version has never been on the FAB, and Git mode changes nowhere).
          <Button
            variant="primary"
            aria-label={deployVersion ? `Deploy ${deployVersion}` : 'Deploy stack'}
            loading={deploy.isPending || isDeploying}
            disabled={deploy.isPending || isDeploying}
            onClick={() => deploy.mutate()}
            className={
              deployVersion
                ? 'h-[52px] rounded-full px-5 shadow-[var(--sh-lg)]'
                : 'size-[52px] rounded-full p-0 shadow-[var(--sh-lg)]'
            }
          >
            {!(deploy.isPending || isDeploying) && <Play />}
            {deployVersion && <span className="font-medium">Deploy {deployVersion}</span>}
          </Button>
        )}
      </div>
    </div>
  )
}

// ── Loading skeleton ─────────────────────────────────────────────────────────────

function StackDetailSkeleton() {
  return (
    <div className="space-y-6">
      <Skeleton variant="line" className="h-4 w-32" />
      <div className="space-y-2">
        <Skeleton variant="line" className="h-8 w-56" />
        <Skeleton variant="line" className="h-4 w-80 max-w-full" />
      </div>
      <Skeleton variant="rect" className="h-14 w-full" />
      <Skeleton variant="line" className="h-9 w-48" />
      <div className="space-y-3">
        <Skeleton variant="rect" className="h-40 w-full" />
        <Skeleton variant="rect" className="h-40 w-full" />
      </div>
    </div>
  )
}
