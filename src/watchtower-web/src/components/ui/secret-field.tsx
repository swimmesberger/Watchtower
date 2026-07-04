import { useState } from 'react'
import { Eye, EyeOff } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from './button'
import { CopyButton } from './copy-button'

export interface SecretFieldProps {
  /** The secret value (webhook token, env value, …). */
  value: string
  /** Show a copy button (default true). */
  copyable?: boolean
  /** Placeholder shown when value is empty. */
  placeholder?: string
  /** Render the value read-only as mono text instead of an input. */
  readOnly?: boolean
  /** Change handler when editable. */
  onChange?: (value: string) => void
  'aria-label'?: string
  className?: string
}

/**
 * Masked mono value with an eye toggle (+ optional copy).
 * - readOnly: renders masked text you can reveal & copy (webhook tokens).
 * - editable: a password/text input (env values, token entry).
 */
export function SecretField({
  value,
  copyable = true,
  placeholder,
  readOnly = false,
  onChange,
  'aria-label': ariaLabel,
  className,
}: SecretFieldProps) {
  const [visible, setVisible] = useState(false)

  return (
    <div
      className={cn(
        'flex items-center gap-1 rounded-md border border-border-strong bg-surface-2 pl-3 pr-1',
        className,
      )}
    >
      {readOnly ? (
        <span className="flex-1 truncate py-2 font-mono text-[13px] text-text">
          {value ? (visible ? value : '•'.repeat(Math.min(value.length, 24))) : (
            <span className="text-text-3">{placeholder ?? '—'}</span>
          )}
        </span>
      ) : (
        <input
          type={visible ? 'text' : 'password'}
          value={value}
          onChange={(e) => onChange?.(e.target.value)}
          placeholder={placeholder}
          aria-label={ariaLabel}
          spellCheck={false}
          autoComplete="off"
          className="min-w-0 flex-1 bg-transparent py-2 font-mono text-[13px] text-text outline-none placeholder:text-text-3"
        />
      )}
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        onClick={() => setVisible((v) => !v)}
        aria-label={visible ? 'Hide value' : 'Show value'}
        className="touch-target"
      >
        {visible ? <EyeOff /> : <Eye />}
      </Button>
      {copyable && value && <CopyButton value={value} />}
    </div>
  )
}
