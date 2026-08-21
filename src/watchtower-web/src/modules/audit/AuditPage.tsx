import { useState } from 'react'
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query'
import { RefreshCw, ScrollText } from 'lucide-react'
import { api } from '@/lib/api'
import type { AuditEvent } from '@/lib/types'
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

/** Sentinel for "no filter" — Radix's Select cannot hold an empty-string value. */
const ANY = '__any__'

/** Rows per request. The server clamps to 500; this is a screenful plus room to scroll. */
const PAGE_SIZE = 100

/**
 * The audit trail: the instance's one record of what happened — what users did (logins, denials,
 * account and policy changes) and what Watchtower did (writes against external control planes, backup
 * runs, settings changes, self-updates), newest first.
 *
 * Read-only by construction — this screen has no mutations at all, because the rows are written by the
 * planes whose acts they record. "Load more" walks a keyset cursor rather than an offset: the trail is
 * append-only and is being written while it is read, so an offset page would shift under the reader and
 * silently skip or repeat rows.
 */
export function AuditPage() {
  const qc = useQueryClient()

  const [category, setCategory] = useState<string>(ANY)
  const [action, setAction] = useState<string>(ANY)
  const [actor, setActor] = useState<string>(ANY)

  const filters = {
    category: category === ANY ? null : category,
    action: action === ANY ? null : action,
    actor: actor === ANY ? null : actor,
  }

  // The filters are part of the key, so changing one starts a fresh first page rather than appending an
  // unrelated result to the rows already on screen.
  const queryKey = ['audit', 'events', filters.category, filters.action, filters.actor] as const

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
    queryFn: ({ pageParam }) =>
      api.audit.listEvents({ ...filters, beforeId: pageParam, limit: PAGE_SIZE }),
    // Null is the server saying "that was the last page" — react-query reads it as no next page.
    getNextPageParam: (lastPage) => lastPage.nextBeforeId,
  })

  const events = data?.pages.flatMap((page) => page.events) ?? []

  // The dropdowns are fed by what the trail contains, not by a frontend list — a category, action or
  // actor a future writer introduces becomes filterable without an edit here.
  const { data: facets } = useQuery({
    queryKey: ['audit', 'facets'],
    queryFn: () => api.audit.facets(),
  })

  const filtered = filters.category != null || filters.action != null || filters.actor != null

  /** Drops every loaded page and re-reads from the newest row — the trail has no live tail. */
  function refresh() {
    qc.resetQueries({ queryKey })
    // The dropdowns are derived from the same rows, so a refresh that found a value nobody had recorded
    // before would otherwise leave it unfilterable until a reload.
    qc.invalidateQueries({ queryKey: ['audit', 'facets'] })
  }

  function clearFilters() {
    setCategory(ANY)
    setAction(ANY)
    setActor(ANY)
  }

  const columns: DataListColumn<AuditEvent>[] = [
    {
      key: 'category',
      header: 'Category',
      cell: (e) => <span className="font-mono text-[13px] text-text-2">{e.category}</span>,
    },
    {
      key: 'action',
      header: 'Action',
      cell: (e) => <ActionBadge event={e} />,
    },
    {
      key: 'actor',
      header: 'Actor',
      // Null for background work and startup hooks — the UI names the instance itself.
      cell: (e) =>
        e.actor ? (
          <span className="text-sm text-text">{e.actor}</span>
        ) : (
          <span className="text-sm text-text-3">system</span>
        ),
    },
    {
      key: 'target',
      header: 'Target',
      cell: (e) => <span className="text-sm text-text-2">{e.target}</span>,
    },
    {
      key: 'detail',
      header: 'Detail',
      className: 'max-w-[420px]',
      cell: (e) => <DetailCell detail={e.error ? `${e.error}${e.detail ? ` — ${e.detail}` : ''}` : e.detail} />,
    },
    {
      key: 'when',
      header: 'When',
      align: 'right',
      className: 'w-px',
      cell: (e) => (
        <span className="tnum whitespace-nowrap text-sm text-text-2" title={absoluteTitle(e.at)}>
          {timeAgo(e.at)}
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
            What happened on this instance — who did what, and what Watchtower did as a result — newest
            first. Reads are never logged; rows are never edited or removed from here.
          </p>
        </div>
        {/* Spins only for a refresh: the first load is already reported by the list's own skeleton rows,
            and "load more" by the button at the foot of the list. */}
        <Button
          variant="secondary"
          onClick={refresh}
          loading={isFetching && !isLoading && !isFetchingNextPage}
        >
          <RefreshCw /> Refresh
        </Button>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <FacetField label="Category" value={category} onChange={setCategory} options={facets?.categories ?? []} any="All categories" />
        <FacetField label="Action" value={action} onChange={setAction} options={facets?.actions ?? []} any="All actions" />
        <FacetField label="Actor" value={actor} onChange={setActor} options={facets?.actors ?? []} any="Everyone" />

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
                    : 'Logins, access decisions, account and settings changes, proxy writes and backup runs will appear here as they happen.'
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

function FacetField({
  label,
  value,
  onChange,
  options,
  any,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  options: string[]
  any: string
}) {
  return (
    <Field label={label} className="w-full sm:w-56">
      {({ id }) => (
        <Select value={value} onValueChange={onChange}>
          <SelectTrigger id={id}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ANY}>{any}</SelectItem>
            {options.map((o) => (
              <SelectItem key={o} value={o}>
                {o}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
    </Field>
  )
}

/**
 * Tone by outcome rather than by subject: the things worth spotting while scrolling are a failure — a
 * rejected login, a refused access, a write Cloudflare bounced (danger) — and an out-of-band recovery
 * (warn). A completed login is the one success worth a colour; everything else is an administrative act,
 * which is normal.
 */
function toneFor(event: AuditEvent): BadgeTone {
  if (!event.success) return 'danger'
  if (event.action === 'auth.breakglass') return 'warn'
  if (event.action === 'login.ok' || event.action === 'login.mfa.ok') return 'ok'
  return 'neutral'
}

function ActionBadge({ event }: { event: AuditEvent }) {
  return (
    <Badge tone={toneFor(event)} className="font-mono">
      {event.action}
    </Badge>
  )
}

/**
 * The detail is free-form and can be long (an actor, a target, a reason, a changed set, an upstream's
 * error). Truncated to keep the row height fixed, with the whole value in a tooltip that a keyboard can
 * reach.
 */
function DetailCell({ detail }: { detail: string | null | undefined }) {
  if (!detail) return <span className="text-sm text-text-3">—</span>
  return (
    <Tooltip label={<span className="whitespace-pre-wrap break-words">{detail}</span>}>
      <span className="block max-w-[420px] truncate text-sm text-text-2" tabIndex={0}>
        {detail}
      </span>
    </Tooltip>
  )
}

function EventCard({ event }: { event: AuditEvent }) {
  return (
    <div className="min-w-0">
      <div className="flex items-center justify-between gap-2">
        <ActionBadge event={event} />
        <span className="tnum text-xs text-text-3" title={absoluteTitle(event.at)}>
          {timeAgo(event.at)}
        </span>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-2 text-sm text-text-2">
        <span className="font-mono text-[13px]">{event.category}</span>
        <span className="text-text-3">·</span>
        <span>{event.actor ?? 'system'}</span>
        <span className="text-text-3">·</span>
        <span>{event.target}</span>
      </div>
      {(event.error || event.detail) && (
        <div className="mt-1 break-words text-xs text-text-3">
          {event.error ? `${event.error}${event.detail ? ` — ${event.detail}` : ''}` : event.detail}
        </div>
      )}
    </div>
  )
}
