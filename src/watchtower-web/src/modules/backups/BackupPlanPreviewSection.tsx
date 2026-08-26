import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, Lock, SlidersHorizontal } from 'lucide-react'
import { api } from '@/lib/api'
import type {
  BackupPlanPreview,
  BackupServiceAction,
  BackupServicePreview,
  Stack,
} from '@/lib/types'
import { Badge, type BadgeTone } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { CopyButton } from '@/components/ui/copy-button'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { SectionHeader } from '@/components/ui/section-header'
import { toast } from '@/components/ui/use-toast'
import { cn } from '@/lib/utils'

// ── Plan preview + per-service overrides (ADR-0020) ─────────────────────────────
// The dry run of the next backup for the stack as deployed right now: what each container gets
// (stop / pause / keep / dump / excluded), why, and where that came from — the mount rule, a compose
// label (read-only here: infrastructure as code wins, as env vars do in Settings) or a UI override.
// Overrides fill in where no label is set and can be promoted to labels with the snippet below.

const ACTION_LABEL: Record<BackupServiceAction, string> = {
  stop: 'Stop',
  pause: 'Pause',
  keep: 'Keep running',
  dump: 'Dump',
  excluded: 'Excluded',
  notRunning: 'Not running',
}

const ACTION_TONE: Record<BackupServiceAction, BadgeTone> = {
  stop: 'warn',
  pause: 'run',
  keep: 'ok',
  dump: 'ok',
  excluded: 'neutral',
  notRunning: 'neutral',
}

export function BackupPlanPreviewSection({ stack }: { stack: Stack }) {
  const qc = useQueryClient()
  const queryKey = ['backups', 'plan-preview', stack.id]

  const { data: preview, isLoading, isError, error, refetch, isFetching } = useQuery({
    queryKey,
    queryFn: () => api.backups.previewPlan(stack.id),
    staleTime: 15_000,
    retry: false,
  })

  const setOverride = useMutation({
    mutationFn: (next: {
      service: string
      exclude: boolean
      stop: string | null
      dump: string | null
    }) =>
      api.backups.setServiceOverride(stack.id, next.service, {
        exclude: next.exclude,
        stop: next.stop,
        dump: next.dump,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey })
      toast.success('Service override saved.')
    },
    onError: (err: Error) => toast.error('Failed to save', err.message),
  })

  const columns: DataListColumn<BackupServicePreview>[] = [
    {
      key: 'service',
      header: 'Service',
      cell: (row) => (
        <span className="flex min-w-0 flex-col">
          <span className="truncate font-medium text-text">{row.service}</span>
          <span className="truncate text-[12px] text-text-3">
            {row.container ?? 'not deployed'}
            {row.container && row.state !== 'running' ? ` · ${row.state}` : ''}
          </span>
        </span>
      ),
    },
    {
      key: 'volumes',
      header: 'Volumes',
      cell: (row) =>
        row.volumes.length === 0 ? (
          <span className="text-text-3">—</span>
        ) : (
          <span className="font-mono text-[12px] text-text-2">{row.volumes.join(', ')}</span>
        ),
    },
    {
      key: 'plan',
      header: 'Next run',
      cell: (row) => (
        <span className="flex min-w-0 flex-col gap-1">
          <span>
            <Badge tone={ACTION_TONE[row.action]}>{ACTION_LABEL[row.action]}</Badge>
          </span>
          <span className="text-[12px] leading-snug text-text-2">{row.reason}</span>
        </span>
      ),
    },
    {
      key: 'source',
      header: 'Set by',
      cell: (row) => <SourceCell row={row} />,
    },
    {
      key: 'override',
      header: '',
      align: 'right',
      cell: (row) => (
        <OverrideMenu
          row={row}
          pending={setOverride.isPending}
          onChange={(next) => setOverride.mutate({ service: row.service, ...next })}
        />
      ),
    },
  ]

  return (
    <div className="space-y-3">
      <SectionHeader
        title="Services"
        action={
          <Button variant="ghost" size="sm" loading={isFetching} onClick={() => refetch()}>
            Refresh
          </Button>
        }
      />
      <p className="-mt-2 text-[13px] text-text-2">
        What the next run does with each container of the stack as it is deployed right now, and why.
        A <code className="font-mono text-[12px]">watchtower.backup.*</code> label in the compose file
        always wins and shows as read-only here; the per-row menu sets the same thing for a service
        without a label, and the snippet below turns those into labels you can commit.
      </p>

      {isError ? (
        <Banner tone="danger" title="Couldn’t preview the plan">
          {error instanceof Error ? error.message : 'The Docker daemon is unreachable.'}
        </Banner>
      ) : !isLoading && preview && !preview.deployed ? (
        <EmptyState
          icon={SlidersHorizontal}
          title="Nothing to preview yet"
          description="Deploy the stack once — the preview reads the running containers and their labels."
        />
      ) : (
        <>
          <DataList
            items={preview?.services ?? []}
            columns={columns}
            getKey={(row) => `${row.service}/${row.container ?? ''}`}
            skeletonRows={isLoading ? 3 : undefined}
            aria-label="Backup plan per service"
            renderCard={(row) => (
              <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-3">
                <div className="flex items-start justify-between gap-2">
                  <span className="flex min-w-0 flex-col">
                    <span className="truncate font-medium text-text">{row.service}</span>
                    <span className="truncate text-[12px] text-text-3">
                      {row.container ?? 'not deployed'}
                    </span>
                  </span>
                  <OverrideMenu
                    row={row}
                    pending={setOverride.isPending}
                    onChange={(next) => setOverride.mutate({ service: row.service, ...next })}
                  />
                </div>
                <div>
                  <Badge tone={ACTION_TONE[row.action]}>{ACTION_LABEL[row.action]}</Badge>
                </div>
                <p className="text-[12px] text-text-2">{row.reason}</p>
                <SourceCell row={row} />
              </div>
            )}
          />
          {preview && <PreviewFooter preview={preview} />}
        </>
      )}
    </div>
  )
}

/** The "Set by" cell: the label (locked), the UI override, or the default rule. */
function SourceCell({ row }: { row: BackupServicePreview }) {
  const labels = [
    row.excludeLabel != null && `watchtower.backup.exclude=${row.excludeLabel}`,
    row.stopLabel != null && `watchtower.backup.stop=${row.stopLabel}`,
    row.dumpLabel != null && `watchtower.backup.dump=${row.dumpLabel}`,
  ].filter((l): l is string => typeof l === 'string')
  const o = row.override
  const overrides = o
    ? [
        o.exclude && 'exclude',
        o.stop != null && `stop=${o.stop}`,
        o.dump != null && `dump=${o.dump}`,
      ].filter((l): l is string => typeof l === 'string')
    : []

  if (labels.length === 0 && overrides.length === 0)
    return <span className="text-[12px] text-text-3">mount rule / stack default</span>

  return (
    <span className="flex flex-col gap-1 text-[12px]">
      {labels.length > 0 && (
        <span
          className="inline-flex items-start gap-1 text-text-2"
          title="Set by a compose label — infrastructure as code wins; change it in the compose file and redeploy."
        >
          <Lock className="mt-0.5 size-3 shrink-0 text-text-3" aria-hidden />
          <span className="font-mono leading-snug">{labels.join('\n')}</span>
        </span>
      )}
      {overrides.length > 0 && (
        <span className={cn('text-text-2', labels.length > 0 && 'text-text-3')}>
          {/* An inherited row is the *template's* setting, not this stack's — saying "UI override"
              over it would send the reader looking for a stack override that does not exist. */}
          {o?.inherited ? 'Template policy: ' : 'UI override: '}
          {overrides.join(', ')}
          {labels.length > 0 && row.source === 'label' ? ' (shadowed by the label)' : ''}
        </span>
      )}
    </span>
  )
}

/**
 * Per-row override menu. Each knob a label already sets is disabled — the label wins, exactly as an
 * env-pinned setting is read-only in Settings (ADR-0014). The menu always writes the whole override,
 * so clearing every knob deletes it.
 */
function OverrideMenu({
  row,
  pending,
  onChange,
}: {
  row: BackupServicePreview
  pending: boolean
  onChange: (next: { exclude: boolean; stop: string | null; dump: string | null }) => void
}) {
  const current = {
    exclude: row.override?.exclude ?? false,
    stop: row.override?.stop ?? null,
    dump: row.override?.dump ?? null,
  }
  const stopLocked = row.stopLabel != null
  const excludeLocked = row.excludeLabel != null
  const dumpLocked = row.dumpLabel != null
  const hasOverride = row.override != null

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant={hasOverride ? 'secondary' : 'ghost'} size="sm" disabled={pending}>
          <SlidersHorizontal /> Override <ChevronDown className="size-3" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="min-w-[15rem]">
        <DropdownMenuLabel>
          During the snapshot{stopLocked ? ' · set by label' : ''}
        </DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={current.stop ?? 'default'}
          onValueChange={(v) =>
            onChange({ ...current, stop: v === 'default' ? null : (v as 'true' | 'false' | 'pause') })
          }
        >
          <DropdownMenuRadioItem value="default" disabled={stopLocked}>
            Stack default
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="true" disabled={stopLocked}>
            Stop
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="pause" disabled={stopLocked}>
            Pause (crash-consistent)
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="false" disabled={stopLocked}>
            Keep running (hot copy)
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
        <DropdownMenuSeparator />
        <DropdownMenuCheckboxItem
          checked={current.exclude}
          disabled={excludeLocked}
          onCheckedChange={(v) => onChange({ ...current, exclude: v === true })}
        >
          Exclude from backup{excludeLocked ? ' · set by label' : ''}
        </DropdownMenuCheckboxItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel>Database dump{dumpLocked ? ' · set by label' : ''}</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={current.dump ?? 'default'}
          onValueChange={(v) =>
            onChange({ ...current, dump: v === 'default' ? null : (v as 'false' | 'postgres') })
          }
        >
          <DropdownMenuRadioItem value="default" disabled={dumpLocked}>
            Detect by image
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="postgres" disabled={dumpLocked}>
            Dump as Postgres
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="false" disabled={dumpLocked}>
            Never dump (snapshot the volume)
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
        {hasOverride && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuCheckboxItem
              checked={false}
              onCheckedChange={() => onChange({ exclude: false, stop: null, dump: null })}
            >
              Clear override
            </DropdownMenuCheckboxItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

/** Archived volumes, exclusions, the planner's warnings, and the overrides as a compose snippet. */
function PreviewFooter({ preview }: { preview: BackupPlanPreview }) {
  return (
    <div className="space-y-3">
      <p className="text-[12px] text-text-2">
        {preview.volumes.length === 0 ? (
          'No volume would be archived.'
        ) : (
          <>
            Archived: <span className="font-mono">{preview.volumes.join(', ')}</span>
          </>
        )}
        {preview.excludedVolumes.length > 0 && (
          <>
            {' '}
            · Left out:{' '}
            {preview.excludedVolumes.map((v, i) => (
              <span key={v.name}>
                {i > 0 && ', '}
                <span className="font-mono">{v.name}</span>{' '}
                <span className="text-text-3">({v.reason === 'dump' ? v.detail : `excluded by ${v.detail}`})</span>
              </span>
            ))}
          </>
        )}
      </p>
      {preview.warnings.length > 0 && (
        <Banner tone="warn" title={`${preview.warnings.length} warning(s) from the planner`}>
          <ul className="list-disc space-y-1 pl-4 text-[12.5px]">
            {preview.warnings.map((w) => (
              <li key={w}>{w}</li>
            ))}
          </ul>
        </Banner>
      )}
      {preview.labelSnippet && (
        <details className="group rounded-lg border border-border bg-surface">
          <summary className="flex cursor-pointer items-center justify-between gap-2 px-3 py-2 text-[13px] font-medium text-text">
            <span>Your overrides as compose labels</span>
            <CopyButton value={preview.labelSnippet} label="Copy" size="sm" />
          </summary>
          <div className="border-t border-border px-3 py-2">
            <p className="mb-2 text-[12px] text-text-2">
              Paste into the compose file and redeploy to version these with the stack; the overrides
              can then be cleared — a label and its override say the same thing.
            </p>
            <pre className="overflow-auto rounded-md bg-surface-2 p-3 font-mono text-[12px] leading-relaxed text-text-2">
              {preview.labelSnippet}
            </pre>
          </div>
        </details>
      )}
    </div>
  )
}
