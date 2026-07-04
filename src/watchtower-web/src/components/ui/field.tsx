import { useId } from 'react'
import { cn } from '@/lib/utils'
import { Label } from './label'

export interface FieldProps {
  label?: string
  /** Marks the label with a required asterisk. */
  required?: boolean
  /** Guidance line shown under the control (A8). */
  hint?: string
  /** Error message; when present the field renders in an error state. */
  error?: string
  className?: string
  /**
   * Render-prop giving the control the wiring ids for label/hint/error.
   * If you don't need the ids, pass plain children instead.
   */
  children: React.ReactNode | ((ids: { id: string; describedBy?: string }) => React.ReactNode)
}

/** Vertical Label + control + hint/error stack with consistent rhythm (gap-1.5). */
export function Field({ label, required, hint, error, className, children }: FieldProps) {
  const id = useId()
  const hintId = `${id}-hint`
  const errorId = `${id}-error`
  const describedBy = error ? errorId : hint ? hintId : undefined

  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      {label && (
        <Label htmlFor={id} required={required}>
          {label}
        </Label>
      )}
      {typeof children === 'function' ? children({ id, describedBy }) : children}
      {error ? (
        <p id={errorId} role="alert" className="text-xs text-danger">
          {error}
        </p>
      ) : (
        hint && (
          <p id={hintId} className="text-xs text-text-3">
            {hint}
          </p>
        )
      )}
    </div>
  )
}
