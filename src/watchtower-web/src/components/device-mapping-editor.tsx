import { useState } from 'react'
import { Plus, Trash2 } from 'lucide-react'
import { Switch } from '@/components/ui/switch'
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
  /** The stack's known compose services, offered as suggestions on the service column. */
  services?: string[]
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
export function DeviceMappingEditor({
  value,
  services = [],
  onChange,
  className,
}: DeviceMappingEditorProps) {
  const serviceListId = 'device-services'
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
      {services.length > 0 && (
        <datalist id={serviceListId}>
          {services.map((service) => (
            <option key={service} value={service} />
          ))}
        </datalist>
      )}
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
                list={services.length > 0 ? serviceListId : undefined}
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

export interface GpuServiceEditorProps {
  /**
   * Services the stack is known to have, from its deployed containers. Empty for a stack that has
   * never been deployed (or whose containers are gone), which is why the "other service" row below
   * always exists — the setting has to be configurable before the first deploy.
   */
  services: string[]
  /** The selected service names. */
  value: string[]
  onChange: (next: string[]) => void
  className?: string
}

/**
 * Controlled picker for the services that receive the host's GPUs (ADR-0031). A toggle per known
 * service rather than a typed name: Watchtower knows the stack's services, and the devices
 * themselves are not a choice — the deploy probes the host and maps whatever mappable GPUs it
 * finds, so "which services" is the only question this control can ask.
 *
 * A selected service the engine does not currently report still gets a row, marked as such: it may
 * be profile-gated or simply not deployed yet, and silently dropping it would erase a stored
 * setting the operator cannot see.
 */
export function GpuServiceEditor({ value, services, onChange, className }: GpuServiceEditorProps) {
  const [extra, setExtra] = useState('')
  const selected = new Set(value)
  const known = new Set(services)
  const rows = [...services, ...value.filter((s) => !known.has(s)).sort()]

  function toggle(service: string, on: boolean) {
    onChange(on ? [...value, service] : value.filter((s) => s !== service))
  }

  function addExtra() {
    const service = extra.trim()
    if (service === '' || selected.has(service)) {
      setExtra('')
      return
    }
    onChange([...value, service])
    setExtra('')
  }

  return (
    <div className={cn('overflow-hidden rounded-md border border-border', className)}>
      {rows.map((service) => (
        <label
          key={service}
          className="flex items-center justify-between gap-3 border-b border-border px-3 py-2.5 last:border-b-0"
        >
          <span className="min-w-0 truncate font-mono text-[13px] text-text">
            {service}
            {!known.has(service) && (
              <span className="ml-2 font-sans text-[12px] text-text-3">not deployed</span>
            )}
          </span>
          <Switch
            checked={selected.has(service)}
            onCheckedChange={(on) => toggle(service, on)}
            aria-label={`Map host GPUs into ${service}`}
          />
        </label>
      ))}

      <div className="flex items-center gap-2 px-3 py-2.5">
        <input
          value={extra}
          onChange={(e) => setExtra(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              // The editor lives inside the Settings form; Enter here means "add", not "save".
              e.preventDefault()
              addExtra()
            }
          }}
          placeholder={rows.length === 0 ? 'service name' : 'another service…'}
          spellCheck={false}
          autoComplete="off"
          aria-label="Add a service for GPU passthrough"
          className="w-full rounded bg-surface-2 px-3 py-1.5 font-mono text-[13px] text-text outline-none placeholder:text-text-3 focus-visible:shadow-[var(--sh-focus)]"
        />
        <button
          type="button"
          onClick={addExtra}
          disabled={extra.trim() === ''}
          className="shrink-0 rounded p-1.5 text-text-3 transition-colors hover:text-text disabled:opacity-40"
          aria-label="Add service"
        >
          <Plus className="size-3.5" />
        </button>
      </div>
    </div>
  )
}
