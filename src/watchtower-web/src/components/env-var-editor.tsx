import { useState } from 'react'
import { Eye, EyeOff, Lock, Plus, Trash2 } from 'lucide-react'
import type { InjectedEnvVar, StackEnvVarInput } from '@/lib/types'
import { cn } from '@/lib/utils'

export interface EnvVarEditorProps {
  /**
   * The DRAFT rows, INCLUDING the trailing blank row. This is a controlled
   * component: the parent holds the array and passes it straight back.
   * Start with `[{ key: '', value: '' }]`.
   */
  value: StackEnvVarInput[]
  onChange: (rows: StackEnvVarInput[]) => void
  /**
   * What Watchtower injects itself on the next deploy, rendered read-only above the
   * editable rows. In the same list on purpose: "which variables does my container
   * actually get" is one question, and answering it in two places invites the reading
   * that the editable list is the whole answer. Omit where no stack is known yet (New
   * Stack), which is also why these are not part of `value` — they are never saved.
   */
  injected?: InjectedEnvVar[]
  className?: string
}

/**
 * Shared controlled env-var editor (New Stack + Stack settings).
 * Rows are [key | secret value | remove]. A blank trailing row auto-appends so
 * there's always an empty row to type into; removing a row keeps that invariant.
 * Per-row show/hide on the value. On mobile each row is a vertical mini-card.
 *
 * Injected rows render first, locked: same grid, muted, no remove button, and the
 * same show/hide on the value so a secret one is not on screen by default.
 *
 * To persist, filter out rows with an empty key:
 *   value.filter(v => v.key.trim() !== '')
 */
export function EnvVarEditor({ value, onChange, injected, className }: EnvVarEditorProps) {
  const [visible, setVisible] = useState<Set<number>>(new Set())
  const [injectedVisible, setInjectedVisible] = useState<Set<string>>(new Set())

  function toggleInjected(name: string) {
    setInjectedVisible((prev) => {
      const s = new Set(prev)
      if (s.has(name)) s.delete(name)
      else s.add(name)
      return s
    })
  }

  function updateRow(i: number, field: 'key' | 'value', val: string) {
    const next = value.map((r, idx) => (idx === i ? { ...r, [field]: val } : r))
    const last = next.at(-1)
    if (!last || last.key !== '' || last.value !== '') next.push({ key: '', value: '' })
    onChange(next)
  }

  function removeRow(i: number) {
    const next = value.filter((_, idx) => idx !== i)
    const tail = next.at(-1)
    if (!tail || tail.key !== '' || tail.value !== '') next.push({ key: '', value: '' })
    onChange(next)
  }

  function toggleVisible(i: number) {
    setVisible((prev) => {
      const s = new Set(prev)
      if (s.has(i)) s.delete(i)
      else s.add(i)
      return s
    })
  }

  return (
    <div className={cn('overflow-hidden rounded-md border border-border', className)}>
      {/* Header (desktop only) */}
      <div className="hidden grid-cols-[1fr_1fr_2.5rem] bg-surface-2 px-3 py-1.5 text-xs font-medium uppercase tracking-[0.04em] text-text-3 md:grid">
        <span>Key</span>
        <span>Value</span>
        <span />
      </div>

      <div>
        {injected?.map((row) => {
          const shown = injectedVisible.has(row.name)
          return (
            <div
              key={`injected:${row.name}`}
              className={cn(
                'border-b border-border bg-surface-2/40',
                'md:grid md:grid-cols-[1fr_1fr_2.5rem] md:items-center',
                'flex flex-col gap-2 p-3 md:gap-0 md:p-0',
              )}
            >
              <div className="flex min-w-0 items-center gap-1.5 px-0 py-0 md:border-r md:border-border md:px-3 md:py-2">
                <span className="truncate font-mono text-[13px] text-text-2">{row.name}</span>
              </div>
              <div className="relative flex min-w-0 items-center md:border-r md:border-border">
                <span
                  className="w-full truncate px-0 py-0 pr-9 font-mono text-[13px] text-text-3 md:px-3 md:py-2"
                  // The value is inert text, not a disabled input: a disabled input still looks
                  // like somewhere to type, which is the misreading this row exists to avoid.
                  title={shown || !row.secret ? row.value : undefined}
                >
                  {row.secret && !shown ? '••••••••••••' : row.value}
                </span>
                {row.secret && (
                  <button
                    type="button"
                    onClick={() => toggleInjected(row.name)}
                    aria-label={shown ? `Hide ${row.name}` : `Show ${row.name}`}
                    className="absolute right-2 text-text-3 transition-colors hover:text-text"
                  >
                    {shown ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
                  </button>
                )}
              </div>
              <div className="flex items-center justify-end md:justify-center">
                <Lock className="size-3.5 text-text-3" aria-label="Injected by Watchtower" />
              </div>
            </div>
          )
        })}
        {value.map((row, i) => {
          const isBlankTrailer = i === value.length - 1
          const showToggle = row.value !== '' || !isBlankTrailer
          return (
            <div
              key={i}
              className={cn(
                'border-b border-border last:border-b-0',
                // desktop: single grid row; mobile: stacked mini-card
                'md:grid md:grid-cols-[1fr_1fr_2.5rem] md:items-center',
                'flex flex-col gap-2 p-3 md:gap-0 md:p-0',
              )}
            >
              <input
                value={row.key}
                onChange={(e) => updateRow(i, 'key', e.target.value)}
                placeholder="NEW_KEY"
                spellCheck={false}
                autoComplete="off"
                aria-label={`Key for variable ${i + 1}`}
                className="w-full rounded bg-surface-2 px-3 py-2 font-mono text-[13px] text-text outline-none placeholder:text-text-3 focus-visible:shadow-[var(--sh-focus)] md:rounded-none md:border-r md:border-border md:bg-transparent md:focus-visible:shadow-none md:focus-visible:bg-surface-2"
              />
              <div className="relative flex items-center md:border-r md:border-border">
                <input
                  value={row.value}
                  onChange={(e) => updateRow(i, 'value', e.target.value)}
                  placeholder="value"
                  spellCheck={false}
                  autoComplete="off"
                  type={visible.has(i) ? 'text' : 'password'}
                  aria-label={`Value for variable ${i + 1}`}
                  className="w-full rounded bg-surface-2 px-3 py-2 pr-9 font-mono text-[13px] text-text outline-none placeholder:text-text-3 focus-visible:shadow-[var(--sh-focus)] md:rounded-none md:bg-transparent md:focus-visible:shadow-none md:focus-visible:bg-surface-2"
                />
                {showToggle && (
                  <button
                    type="button"
                    onClick={() => toggleVisible(i)}
                    aria-label={visible.has(i) ? 'Hide value' : 'Show value'}
                    className="absolute right-2 text-text-3 transition-colors hover:text-text"
                  >
                    {visible.has(i) ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
                  </button>
                )}
              </div>
              <div className="flex items-center justify-end md:justify-center">
                {!isBlankTrailer ? (
                  <button
                    type="button"
                    onClick={() => removeRow(i)}
                    aria-label={`Remove ${row.key || `variable ${i + 1}`}`}
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
