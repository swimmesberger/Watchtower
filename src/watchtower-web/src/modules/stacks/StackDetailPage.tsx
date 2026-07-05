import { useCallback, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getRouteApi, Link, useParams } from '@tanstack/react-router'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { ChevronRight, Play } from 'lucide-react'
import { stackDetailTabs, type HistoryRowControls } from '@/platform/points'
import { api } from '@/lib/api'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/use-toast'

const routeApi = getRouteApi('/stacks/$id')

export function StackDetailPage() {
  const { id } = useParams({ from: '/stacks/$id' })
  const stackId = Number(id)
  const qc = useQueryClient()

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

  const deploy = useMutation({
    mutationFn: () => api.stacks.deploy(stackId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stacks'] })
      qc.invalidateQueries({ queryKey: ['stacks', stackId, 'events'] })
      toast.info(`Deploying ${stack?.name ?? 'stack'}…`)
    },
    onError: (err: Error) => toast.error('Deploy failed', err.message),
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
      <div className="mx-auto max-w-[1200px]">
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

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 pb-24 md:pb-0">
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
          <p className="mt-1 truncate font-mono text-[12.5px] text-text-2">
            {stack.repositoryUrl} · {stack.branch} · {stack.composeFilePath}
          </p>
        </div>
        {/* Desktop deploy button; mobile uses the FAB below. */}
        <Button
          variant="primary"
          loading={deploy.isPending || isDeploying}
          disabled={deploy.isPending || isDeploying}
          onClick={() => deploy.mutate()}
          className="hidden md:inline-flex"
        >
          {!(deploy.isPending || isDeploying) && <Play />}
          Deploy
        </Button>
      </div>

      {/* Status banner hero */}
      {isDeploying ? (
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

      {/* Mobile Deploy FAB (52px, above the bottom tab bar) */}
      <div className="fixed bottom-bottombar right-4 z-20 mb-3 md:hidden">
        <Button
          variant="primary"
          aria-label="Deploy stack"
          loading={deploy.isPending || isDeploying}
          disabled={deploy.isPending || isDeploying}
          onClick={() => deploy.mutate()}
          className="size-[52px] rounded-full p-0 shadow-[var(--sh-lg)]"
        >
          {!(deploy.isPending || isDeploying) && <Play />}
        </Button>
      </div>
    </div>
  )
}

// ── Loading skeleton ─────────────────────────────────────────────────────────────

function StackDetailSkeleton() {
  return (
    <div className="mx-auto max-w-[1200px] space-y-6">
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
