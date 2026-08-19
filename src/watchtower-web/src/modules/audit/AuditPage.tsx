import { useState } from 'react'
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query'
import { RefreshCw, ScrollText } from 'lucide-react'
import { api } from '@/lib/api'
import type { AuthEvent } from '@/lib/types'
import { timeAgo, absoluteTitle } from '@/lib/format'
import { Button } from '@/components/ui/button'
import { Badge, type BadgeTone } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Field } from '@/components/ui/field'
import { Tooltip } from '@/components/ui/tooltip'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { auditRoute } from './module'

/** Sentinel for "no filter" — Radix's Select cannot hold an empty-string value. */
const ANY = '__any__'

/** Rows per request. The server clamps to 500; this is a screenful plus room to scroll. */
const PAGE_SIZE = 100

/**
 * The audit trail: every login, denial, policy change and break-glass recovery the access-control plane
 * has recorded (docs/central-auth/design.md §3).
 *
 * Read-only by construction — this screen has no mutations at all, because the rows are written by the
 * modules whose acts they record. "Load more" walks a keyset cursor rather than an offset: the trail is
 * append-only and is being written while it is read, so an offset page would shift under the reader and
 * silently skip or repeat rows.
 */
export function AuditPage() {
  const qc = useQueryClient()
  const { caps } = auditRoute.useRouteContext()

  const [kind, setKind] = useState<string>(ANY)
  const [userId, setUserId] = useState<string>(ANY)
  const [routeId, setRouteId] = useState<string>(ANY)

  const filters = {
    kind: kind === ANY ? null : kind,
    userId: userId === ANY ? null : Number(userId),
    routeId: routeId === ANY ? null : Number(routeId),
  }

  // The filters are part of the key, so changing one starts a fresh first page rather than appending an
  // unrelated result to the rows already on screen.
  const queryKey = ['audit', filters.kind, filters.userId, filters.routeId] as const

  const {
    data,
    isLoading,
    isError,
    isFetching,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery({
    queryKey,
    initialPageParam: null as number | null,
    queryFn: ({ pageParam }) => api.audit.list({ ...filters, beforeId: pageParam, limit: PAGE_SIZE }),
    // Null is the server saying "that was the last page" — react-query reads it as no next page.
    getNextPageParam: (lastPage) => lastPage.nextBeforeId,
  })

  const events = data?.pages.flatMap((page) => page.events) ?? []

  // The dropdown is fed by what the trail contains, not by a frontend list of kinds — a kind a future
  // writer introduces becomes filterable without an edit here.
  const { data: kinds = [] } = useQuery({
    queryKey: ['audit', 'kinds'],
    queryFn: () => api.audit.kinds(),
  })

  // Both pickers borrow the rosters the Users and Routes screens already cache. Gated on the module being
  // enabled: with it off the handler is not registered, so asking would only produce a failed call.
  const { data: users = [] } = useQuery({
    queryKey: ['users'],
    queryFn: () => api.users.list(),
    enabled: caps.isModuleEnabled('Users'),
  })
  const { data: routes = [] } = useQuery({
    queryKey: ['routes'],
    queryFn: () => api.proxy.listRoutes(),
    enabled: caps.isModuleEnabled('Proxy'),
  })

  const filtered = filters.kind != null || filters.userId != null || filters.routeId != null

  /** Drops every loaded page and re-reads from the newest row — the trail has no live tail. */
  function refresh() {
    qc.resetQueries({ queryKey })
  }

  function clearFilters() {
    setKind(ANY)
    setUserId(ANY)
    setRouteId(ANY)
  }

  const columns: DataListColumn<AuthEvent>[] = [
    {
      key: 'kind',
      header: 'Event',
      cell: (e) => <KindBadge kind={e.kind} />,
    },
    {
      key: 'user',
      header: 'User',
      // Null once the account is deleted — the trail outlives its subjects, and the name it had lives on
      // in the detail.
      cell: (e) =>
        e.userName ? (
          <span className="text-sm text-text">{e.userName}</span>
        ) : (
          <span className="text-sm text-text-3">—</span>
        ),
    },
    {
      key: 'route',
      header: 'App',
      cell: (e) =>
        e.routeDomain ? (
          <span className="text-sm text-text-2">{e.routeDomain}</span>
        ) : (
          <span className="text-sm text-text-3">—</span>
        ),
    },
    {
      key: 'detail',
      header: 'Detail',
      className: 'max-w-[420px]',
      cell: (e) => <DetailCell detail={e.detail} />,
    },
    {
      key: 'when',
      header: 'When',
      align: 'right',
      className: 'w-px',
      cell: (e) => (
        <span className="tnum whitespace-nowrap text-sm text-text-2" title={absoluteTitle(e.createdAt)}>
          {timeAgo(e.createdAt)}
        </span>
      ),
    },
  ]

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-6 px-4 py-6 md:px-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Audit</h1>
          <p className="mt-1 text-sm text-text-2">
            Logins, access denials and policy changes, newest first. Rows are never edited or removed
            from here.
          </p>
        </div>
        <Button variant="secondary" onClick={refresh} loading={isFetching && !isFetchingNextPage}>
          <RefreshCw /> Refresh
        </Button>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <Field label="Event" className="w-full sm:w-56">
          {({ id }) => (
            <Select value={kind} onValueChange={setKind}>
              <SelectTrigger id={id}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>All events</SelectItem>
                {kinds.map((k) => (
                  <SelectItem key={k} value={k}>
                    {k}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        </Field>

        {caps.isModuleEnabled('Users') && (
          <Field label="User" className="w-full sm:w-56">
            {({ id }) => (
              <Select value={userId} onValueChange={setUserId}>
                <SelectTrigger id={id}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ANY}>All users</SelectItem>
                  {users.map((u) => (
                    <SelectItem key={u.id} value={String(u.id)}>
                      {u.userName}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </Field>
        )}

        {caps.isModuleEnabled('Proxy') && (
          <Field label="App" className="w-full sm:w-64">
            {({ id }) => (
              <Select value={routeId} onValueChange={setRouteId}>
                <SelectTrigger id={id}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ANY}>All apps</SelectItem>
                  {routes.map((r) => (
                    <SelectItem key={r.id} value={String(r.id)}>
                      {r.domain}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </Field>
        )}

        {filtered && (
          <Button variant="ghost" onClick={clearFilters}>
            Clear filters
          </Button>
        )}
      </div>

      {isError ? (
        <Banner
          tone="danger"
          title="Couldn't load the audit trail"
          action={
            <Button variant="link" onClick={refresh}>
              Retry
            </Button>
          }
        >
          Something went wrong while fetching the events.
        </Banner>
      ) : (
        <>
          <DataList
            items={events}
            getKey={(e) => e.id}
            columns={columns}
            renderCard={(e) => <EventCard event={e} />}
            skeletonRows={isLoading ? 8 : undefined}
            emptyState={
              <EmptyState
                icon={ScrollText}
                title={filtered ? 'No matching events' : 'Nothing recorded yet'}
                description={
                  filtered
                    ? 'No event in the trail matches these filters.'
                    : 'Logins, access denials and policy changes will appear here as they happen.'
                }
                action={
                  filtered ? (
                    <Button variant="secondary" onClick={clearFilters}>
                      Clear filters
                    </Button>
                  ) : undefined
                }
              />
            }
            aria-label="Audit events"
          />

          {hasNextPage && (
            <div className="flex justify-center">
              <Button
                variant="secondary"
                onClick={() => fetchNextPage()}
                loading={isFetchingNextPage}
              >
                Load more
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  )
}

/**
 * Tone by outcome rather than by subject: the two things worth spotting while scrolling are a refusal
 * (danger) and an out-of-band recovery (warn). Everything else is an administrative act, which is normal.
 */
function toneFor(kind: string): BadgeTone {
  if (kind === 'login.failed' || kind === 'access.denied') return 'danger'
  if (kind === 'auth.breakglass') return 'warn'
  if (kind === 'login.ok') return 'ok'
  return 'neutral'
}

function KindBadge({ kind }: { kind: string }) {
  return (
    <Badge tone={toneFor(kind)} className="font-mono">
      {kind}
    </Badge>
  )
}

/**
 * The detail is free-form and can be long (an actor, a target, a reason, a changed set). Truncated to keep
 * the row height fixed, with the whole value in a tooltip that a keyboard can reach.
 */
function DetailCell({ detail }: { detail: string | null }) {
  if (!detail) return <span className="text-sm text-text-3">—</span>
  return (
    <Tooltip label={<span className="whitespace-pre-wrap break-words">{detail}</span>}>
      <span className="block max-w-[420px] truncate text-sm text-text-2" tabIndex={0}>
        {detail}
      </span>
    </Tooltip>
  )
}

function EventCard({ event }: { event: AuthEvent }) {
  return (
    <div className="min-w-0">
      <div className="flex items-center justify-between gap-2">
        <KindBadge kind={event.kind} />
        <span className="tnum text-xs text-text-3" title={absoluteTitle(event.createdAt)}>
          {timeAgo(event.createdAt)}
        </span>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-2 text-sm text-text-2">
        <span>{event.userName ?? '—'}</span>
        <span className="text-text-3">·</span>
        <span>{event.routeDomain ?? '—'}</span>
      </div>
      {event.detail && (
        <div className="mt-1 break-words text-xs text-text-3">{event.detail}</div>
      )}
    </div>
  )
}
