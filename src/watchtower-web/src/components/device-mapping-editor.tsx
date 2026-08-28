import { Plus, Trash2 } from 'lucide-react'
import { cn } from '@/lib/utils'

/** One draft row; all fields plain strings so the inputs stay controlled. */
export interface DeviceMappingRow {
  service: string
  hostPath: string
  containerPath: string
  permissions: string
}

export const blankDeviceRow: DeviceMappingRow = {
  service: '',
  hostPath: '',
  containerPath: '',
  permissions: '',
}

function isBlank(row: DeviceMappingRow): boolean {
  return (
    row.service.trim() === '' &&
    row.hostPath.trim() === '' &&
    row.containerPath.trim() === '' &&
    row.permissions.trim() === ''
  )
}

export interface DeviceMappingEditorProps {
  /**
   * The DRAFT rows, INCLUDING the trailing blank row — the EnvVarEditor contract: the parent holds
   * the array and passes it straight back. Start with `[blankDeviceRow]`.
   */
  value: DeviceMappingRow[]
  onChange: (rows: DeviceMappingRow[]) => void
  className?: string
}

const cellClass =
  'w-full rounded bg-surface-2 px-3 py-2 font-mono text-[13px] text-text outline-none placeholder:text-text-3 focus-visible:shadow-[var(--sh-focus)] md:rounded-none md:border-r md:border-border md:bg-transparent md:focus-visible:shadow-none md:focus-visible:bg-surface-2'

/**
 * Controlled editor for a stack's host device mappings (ADR-0030). Rows are
 * [service | host path | container path | permissions | remove]; the blank trailing row
 * auto-appends so there's always an empty row to type into. Container path and permissions are
 * optional — blank means "same path in the container" and "Docker's default (rwm)".
 *
 * To persist, drop fully blank rows: `value.filter(r => !isRowBlank(r))` via the parent.
 */
export function DeviceMappingEditor({ value, onChange, className }: DeviceMappingEditorProps) {
  function updateRow(i: number, field: keyof DeviceMappingRow, val: string) {
    const next = value.map((r, idx) => (idx === i ? { ...r, [field]: val } : r))
    const last = next.at(-1)
    if (!last || !isBlank(last)) next.push(blankDeviceRow)
    onChange(next)
  }

  function removeRow(i: number) {
    const next = value.filter((_, idx) => idx !== i)
    const tail = next.at(-1)
    if (!tail || !isBlank(tail)) next.push(blankDeviceRow)
    onChange(next)
  }

  const grid = 'md:grid-cols-[1fr_1.4fr_1.4fr_5rem_2.5rem]'

  return (
    <div className={cn('overflow-hidden rounded-md border border-border', className)}>
      {/* Header (desktop only) */}
      <div
        className={cn(
          'hidden bg-surface-2 px-3 py-1.5 text-xs font-medium uppercase tracking-[0.04em] text-text-3 md:grid',
          grid,
        )}
      >
        <span>Service</span>
        <span>Host device</span>
        <span>In container</span>
        <span>Access</span>
        <span />
      </div>

      <div>
        {value.map((row, i) => {
          const isBlankTrailer = i === value.length - 1
          return (
            <div
              key={i}
              className={cn(
                'border-b border-border last:border-b-0',
                'md:grid md:items-center',
                grid,
                'flex flex-col gap-2 p-3 md:gap-0 md:p-0',
              )}
            >
              <input
                value={row.service}
                onChange={(e) => updateRow(i, 'service', e.target.value)}
                placeholder="service"
                spellCheck={false}
                autoComplete="off"
                aria-label={`Service for device ${i + 1}`}
                className={cellClass}
              />
              <input
                value={row.hostPath}
                onChange={(e) => updateRow(i, 'hostPath', e.target.value)}
                placeholder="/dev/dri/renderD128"
                spellCheck={false}
                autoComplete="off"
                aria-label={`Host device path for device ${i + 1}`}
                className={cellClass}
              />
              <input
                value={row.containerPath}
                onChange={(e) => updateRow(i, 'containerPath', e.target.value)}
                placeholder="same as host"
                spellCheck={false}
                autoComplete="off"
                aria-label={`Container path for device ${i + 1}`}
                className={cellClass}
              />
              <input
                value={row.permissions}
                onChange={(e) => updateRow(i, 'permissions', e.target.value)}
                placeholder="rwm"
                spellCheck={false}
                autoComplete="off"
                maxLength={3}
                aria-label={`Permissions for device ${i + 1}`}
                className={cellClass}
              />
              <div className="flex items-center justify-end md:justify-center">
                {!isBlankTrailer ? (
                  <button
                    type="button"
                    onClick={() => removeRow(i)}
                    aria-label={`Remove ${row.hostPath || `device ${i + 1}`}`}
                    className="rounded p-1.5 text-danger transition-colors hover:bg-danger-bg"
                  >
                    <Trash2 className="size-3.5" />
                  </button>
                ) : (
                  <Plus className="size-3.5 text-text-3" aria-hidden />
                )}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

export function isDeviceRowBlank(row: DeviceMappingRow): boolean {
  return isBlank(row)
}
