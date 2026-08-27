import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRouteContext } from '@tanstack/react-router'
import { api } from '@/lib/api'
import type { RecoveryStack, RevivalStatus } from '@/lib/types'
import { timeAgo, absoluteTitle } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { toast } from '@/components/ui/use-toast'

/** How each revival state reads, and which badge tone carries it. */
const STATUS: Record<RevivalStatus, { label: string; tone: 'neutral' | 'run' | 'ok' | 'danger' }> = {
  pending: { label: 'waiting', tone: 'neutral' },
  deploying: { label: 'deploying', tone: 'run' },
  restoring: { label: 'restoring', tone: 'run' },
  done: { label: 'back', tone: 'ok' },
  failed: { label: 'failed', tone: 'danger' },
  skipped: { label: 'skipped', tone: 'neutral' },
}

/**
 * The checklist after an instance restore (ADR-0027): each stack is deployed from git — its definition
 * arrived with the restored database — and then restored from its newest archive.
 *
 * The two steps are one action because the order matters and is easy to get wrong by hand: only a
 * deploy creates the volumes, and a deploy on its own leaves the stack running with empty ones.
 */
export function RecoveryChecklistCard() {
  const qc = useQueryClient()
  // Reviving a stack deploys and restores it, which is admin-only on the server.
  const { caps } = useRouteContext({ from: '__root__' })
  const isAdmin = caps.hasRole('Admin')

  const { data: checklist } = useQuery({
    queryKey: ['backups', 'recovery'],
    queryFn: api.backups.getRecoveryChecklist,
    enabled: isAdmin,
  })

  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ['backups', 'recovery'] })
    void qc.invalidateQueries({ queryKey: ['backups', 'restoreStatus'] })
    void qc.invalidateQueries({ queryKey: ['stacks'] })
  }

  const revive = useMutation({
    mutationFn: api.backups.reviveStack,
    onSuccess: stack => {
      invalidate()
      if (stack.status === 'failed') toast.error(`${stack.name}: ${stack.detail}`)
      else toast.success(`${stack.name}: ${stack.detail}`)
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'The stack could not be revived.'),
  })

  const reviveAll = useMutation({
    mutationFn: api.backups.reviveAll,
    onSuccess: result => {
      invalidate()
      toast.success(`${result.revived} stack${result.revived === 1 ? '' : 's'} deployed and restored.`)
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'The stacks could not be revived.'),
  })

  const skip = useMutation({
    mutationFn: api.backups.skipRecoveryStack,
    onSuccess: invalidate,
    onError: err => toast.error(err instanceof Error ? err.message : 'The stack could not be skipped.'),
  })

  const dismiss = useMutation({
    mutationFn: api.backups.dismissRecovery,
    onSuccess: () => {
      invalidate()
      toast.success('Checklist dismissed.')
    },
    onError: err => toast.error(err instanceof Error ? err.message : 'The checklist could not be dismissed.'),
  })

  if (!isAdmin || !checklist || checklist.dismissed) return null

  const busy = revive.isPending || reviveAll.isPending
  const outstanding = checklist.stacks.filter(s => s.status === 'pending' || s.status === 'failed')
  const revivingId = revive.variables

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text">Bring the stacks back</h2>
        <p className="mt-0.5 text-[13px] text-text-2">
          This Watchtower was restored from a backup of{' '}
          <span className="font-mono">{checklist.sourceInstance}</span>{' '}
          <span title={absoluteTitle(checklist.restoredAtUtc)}>{timeAgo(checklist.restoredAtUtc)}</span>.
          Each stack is deployed from git and then restored from its newest archive — in that order,
          because only the deploy creates the volumes the restore needs.
        </p>
      </div>

      <Card>
        <CardContent className="flex flex-col gap-4">
          <ul className="divide-y divide-border">
            {checklist.stacks.map(stack => (
              <ChecklistRow
                key={stack.stackId}
                stack={stack}
                busy={busy}
                working={revivingId === stack.stackId && revive.isPending}
                onRevive={() => revive.mutate(stack.stackId)}
                onSkip={() => skip.mutate(stack.stackId)}
              />
            ))}
          </ul>

          <div className="flex flex-wrap items-center gap-3">
            <Button
              variant="primary"
              size="sm"
              disabled={busy || outstanding.length === 0}
              loading={reviveAll.isPending}
              onClick={() => reviveAll.mutate()}
            >
              Revive all ({outstanding.length})
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={busy || dismiss.isPending}
              loading={dismiss.isPending}
              onClick={() => dismiss.mutate()}
            >
              Dismiss checklist
            </Button>
            {busy && (
              <span className="text-[13px] text-text-2">
                Deploying and restoring — this takes as long as the deploys do.
              </span>
            )}
          </div>
        </CardContent>
      </Card>
    </section>
  )
}

function ChecklistRow({
  stack,
  busy,
  working,
  onRevive,
  onSkip,
}: {
  stack: RecoveryStack
  busy: boolean
  working: boolean
  onRevive: () => void
  onSkip: () => void
}) {
  const status = STATUS[stack.status]
  const outstanding = stack.status === 'pending' || stack.status === 'failed'

  return (
    <li className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2.5">
      <Badge tone={status.tone} size="sm">
        {status.label}
      </Badge>
      <span className="min-w-0 flex-1 truncate text-[13px] font-medium text-text">{stack.name}</span>
      {stack.detail && <span className="text-[13px] text-text-2">{stack.detail}</span>}
      {outstanding && (
        <span className="flex items-center gap-2">
          <Button size="sm" variant="secondary" disabled={busy} loading={working} onClick={onRevive}>
            {stack.status === 'failed' ? 'Try again' : 'Revive'}
          </Button>
          <Button size="sm" variant="ghost" disabled={busy} onClick={onSkip}>
            Skip
          </Button>
        </span>
      )}
    </li>
  )
}
