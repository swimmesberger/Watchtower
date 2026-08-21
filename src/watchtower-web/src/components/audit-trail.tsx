// The audit trail's one list renderer, shared by the global Audit page (no category — everything)
// and by contextual embeds that scope it to their plane (Routes → Audit shows `category="proxy"`).
// A future plane that records audit events gets its own embed by rendering this with its category;
// the global page picks the events up with no changes at all.
import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { timeAgo } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { SectionHeader } from '@/components/ui/section-header'
import { Skeleton } from '@/components/ui/skeleton'

export function AuditTrailCard({
  category,
  title,
  description,
  emptyText,
  showCategory = false,
}: {
  /** Category prefix to narrow to (e.g. `proxy`); omit for the full trail. */
  category?: string
  title: string
  description: string
  emptyText: string
  /** Show each event's category — useful on the global page, noise on a scoped embed. */
  showCategory?: boolean
}) {
  const { data: events = [], isLoading, isError, refetch, isFetching } = useQuery({
    queryKey: ['audit', category ?? 'all'],
    queryFn: () => api.audit.listEvents(category ?? null, 200),
    staleTime: 30_000,
  })

  if (isLoading) {
    return (
      <Card>
        <CardContent className="flex flex-col gap-3 p-5">
          <Skeleton variant="line" className="w-2/3" />
          <Skeleton variant="line" className="w-1/2" />
          <Skeleton variant="line" className="w-3/5" />
        </CardContent>
      </Card>
    )
  }
  if (isError) {
    return (
      <Banner
        tone="danger"
        title="Couldn’t load the audit trail"
        action={
          <Button variant="link" onClick={() => refetch()}>
            Retry
          </Button>
        }
      >
        The audit log is unavailable.
      </Banner>
    )
  }

  return (
    <Card>
      <CardContent className="p-5">
        <div className="flex items-start justify-between gap-3">
          <SectionHeader title={title} description={description} />
          <Button size="sm" variant="secondary" onClick={() => refetch()} loading={isFetching}>
            Refresh
          </Button>
        </div>
        {events.length === 0 ? (
          <p className="py-6 text-center text-[13px] text-text-2">{emptyText}</p>
        ) : (
          <ul className="divide-y divide-border">
            {events.map((e) => (
              <li key={e.id} className="flex flex-wrap items-start justify-between gap-x-4 gap-y-1 py-2.5">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    {showCategory && (
                      <span className="font-mono text-xs text-text-3">{e.category}</span>
                    )}
                    <Badge tone={e.success ? 'neutral' : 'danger'} size="sm">
                      <span className="font-mono">{e.action}</span>
                    </Badge>
                    <span className="truncate font-medium text-text">{e.target}</span>
                  </div>
                  {e.detail && <p className="mt-0.5 text-[13px] text-text-2">{e.detail}</p>}
                  {e.error && <p className="mt-0.5 text-[13px] text-danger">{e.error}</p>}
                </div>
                <span className="shrink-0 text-xs text-text-3" title={e.at}>
                  {timeAgo(e.at)} · {e.actor ?? 'system'}
                </span>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}
