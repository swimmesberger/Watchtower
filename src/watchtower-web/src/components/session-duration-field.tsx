import { useState } from 'react'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

/**
 * The durations Cloudflare's own dashboard offers for an Access application, in the duration syntax its API
 * takes. Offered first so the common answers are a pick rather than a format to remember; anything else the
 * API accepts is still one "Custom…" away.
 */
export const SESSION_DURATION_PRESETS: { value: string; label: string }[] = [
  { value: '0s', label: 'No duration, expires immediately' },
  { value: '15m', label: '15 minutes' },
  { value: '30m', label: '30 minutes' },
  { value: '6h', label: '6 hours' },
  { value: '12h', label: '12 hours' },
  { value: '24h', label: '24 hours' },
  { value: '168h', label: '1 week' },
  { value: '730h', label: '1 month' },
]

// Radix Select reserves the empty string, so "use the default" and "type one" need values of their own.
const DEFAULT = '__default'
const CUSTOM = '__custom'

const isPreset = (value: string) => SESSION_DURATION_PRESETS.some((p) => p.value === value)

/** The preset's label for a duration, or the raw value when it is not one of them. */
export function describeSessionDuration(value: string): string {
  return SESSION_DURATION_PRESETS.find((p) => p.value === value)?.label ?? value
}

/**
 * A Cloudflare Access session duration: a preset from the list, the default (the empty string), or a
 * custom duration typed in Cloudflare's syntax. A stored value that is not a preset opens as custom, so
 * what was saved is always what is shown.
 */
export function SessionDurationField({
  id,
  describedBy,
  value,
  onChange,
  defaultLabel,
  disabled,
}: {
  id?: string
  describedBy?: string
  /** A duration such as `8h`, or `''` for the default. */
  value: string
  onChange: (value: string) => void
  /** What the empty value means here, e.g. "Default (24 hours)". */
  defaultLabel: string
  disabled?: boolean
}) {
  const [typing, setTyping] = useState(false)
  const trimmed = value.trim()
  const custom = typing || (trimmed !== '' && !isPreset(trimmed))

  if (custom) {
    return (
      <div className="flex flex-col gap-1.5">
        <Input
          id={id}
          aria-describedby={describedBy}
          mono
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="e.g. 2h45m"
          spellCheck={false}
          disabled={disabled}
          autoFocus={typing}
        />
        {!disabled && (
          <button
            type="button"
            className="self-start text-xs text-text-3 transition-colors hover:text-text-2"
            onClick={() => {
              setTyping(false)
              // Back to a value the list can show; one it cannot would reopen this input straight away.
              if (!isPreset(value.trim())) onChange('')
            }}
          >
            Choose from list
          </button>
        )}
      </div>
    )
  }

  return (
    <Select
      value={trimmed === '' ? DEFAULT : trimmed}
      disabled={disabled}
      onValueChange={(v) => {
        if (v === CUSTOM) {
          setTyping(true)
        } else {
          onChange(v === DEFAULT ? '' : v)
        }
      }}
    >
      <SelectTrigger id={id} aria-describedby={describedBy}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={DEFAULT}>{defaultLabel}</SelectItem>
        {SESSION_DURATION_PRESETS.map((p) => (
          <SelectItem key={p.value} value={p.value}>
            {p.label}
          </SelectItem>
        ))}
        <SelectItem value={CUSTOM}>Custom…</SelectItem>
      </SelectContent>
    </Select>
  )
}
