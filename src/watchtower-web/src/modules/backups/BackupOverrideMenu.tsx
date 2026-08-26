import { ChevronDown, SlidersHorizontal } from 'lucide-react'
import { Button } from '@/components/ui/button'
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

/** The three knobs of a per-service override, in the compose labels' own value syntax (ADR-0020). */
export interface BackupOverrideValue {
  exclude: boolean
  stop: string | null
  dump: string | null
}

/** Which knobs a compose label already decides, and are therefore read-only here. */
export interface BackupOverrideLocks {
  exclude?: boolean
  stop?: boolean
  dump?: boolean
}

/**
 * The per-service override menu, shared by the stack's plan preview and the product's fleet policy.
 *
 * One control for both rungs of the ladder, deliberately: a stack row and a template row hold the same
 * three values with the same three-way "not set" semantics, and two menus that drifted apart would be
 * two answers to one question. What differs is only what "not set" *falls back to*, which is
 * {@link scopeDefaultLabel}, and which knobs a label has taken away, which is {@link locked}.
 *
 * The menu always writes the **whole** override — both setters replace it outright — so clearing every
 * knob deletes the row.
 */
export function BackupOverrideMenu({
  value,
  locked,
  pending,
  scopeDefaultLabel,
  note,
  onChange,
}: {
  /** The stored override, or null when the service has none. */
  value: BackupOverrideValue | null
  locked?: BackupOverrideLocks
  pending: boolean
  /** What the "not set" radio row is called — "Stack default" or "Fleet default". */
  scopeDefaultLabel: string
  /**
   * A consequence line shown above the controls — before the click, not after it. Used where the ladder
   * makes one: a stack row *replaces* the template's whole row for the service (invariant 18), which is
   * not something the menu's own state can show.
   */
  note?: string
  onChange: (next: BackupOverrideValue) => void
}) {
  const current: BackupOverrideValue = {
    exclude: value?.exclude ?? false,
    stop: value?.stop ?? null,
    dump: value?.dump ?? null,
  }
  const stopLocked = locked?.stop === true
  const excludeLocked = locked?.exclude === true
  const dumpLocked = locked?.dump === true
  const hasOverride = value != null

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant={hasOverride ? 'secondary' : 'ghost'} size="sm" disabled={pending}>
          <SlidersHorizontal /> Override <ChevronDown className="size-3" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="min-w-[15rem]">
        {note && (
          <>
            <DropdownMenuLabel className="font-normal text-text-2">{note}</DropdownMenuLabel>
            <DropdownMenuSeparator />
          </>
        )}
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
            {scopeDefaultLabel}
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

/** "exclude, stop=pause, dump=postgres" — the knobs an override actually sets, or null when none does. */
export function describeOverride(value: BackupOverrideValue | null): string | null {
  if (value == null) return null
  const parts = [
    value.exclude && 'exclude',
    value.stop != null && `stop=${value.stop}`,
    value.dump != null && `dump=${value.dump}`,
  ].filter((p): p is string => typeof p === 'string')
  return parts.length > 0 ? parts.join(', ') : null
}
