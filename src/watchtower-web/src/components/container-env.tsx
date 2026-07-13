import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, Eye, EyeOff } from 'lucide-react'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'

interface ContainerEnvProps {
  containerId: string
  containerName: string
}

/**
 * Collapsible viewer for the env vars a container is actually running with
 * (Docker inspect: image ENV + compose interpolation applied). Fetches lazily
 * on first expand; values are masked by default with a per-row reveal since
 * they routinely contain secrets.
 */
export function ContainerEnv({ containerId, containerName }: ContainerEnvProps) {
  const [open, setOpen] = useState(false)
  const [visible, setVisible] = useState<Set<string>>(new Set())

  const { data: envVars, isPending, isError, error } = useQuery({
    queryKey: ['containers', containerId, 'env'],
    queryFn: () => api.containers.env(containerId),
    enabled: open,
    staleTime: 30_000,
  })

  function toggleVisible(key: string) {
    setVisible((prev) => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  return (
    <div className="overflow-hidden rounded-md border border-border">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className={cn(
          'flex w-full items-center gap-2 bg-surface-2 px-3.5 py-2.5 text-left font-mono text-sm',
          'transition-colors hover:bg-surface-3',
          'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
        )}
      >
        {open ? (
          <ChevronDown className="size-4 shrink-0 text-text-3" />
        ) : (
          <ChevronRight className="size-4 shrink-0 text-text-3" />
        )}
        <span className="flex-1 truncate text-text-2">{containerName}</span>
        <span className="text-[11px] font-medium text-text-3">
          {open ? 'Hide environment' : 'Environment'}
        </span>
      </button>

      {open && (
        <div className="border-t border-border">
          {isPending && <p className="px-3.5 py-2.5 text-sm text-text-3">Loading environment…</p>}
          {isError && (
            <p className="px-3.5 py-2.5 text-sm text-danger">
              Failed to load environment: {error.message}
            </p>
          )}
          {envVars && envVars.length === 0 && (
            <p className="px-3.5 py-2.5 text-sm text-text-3">No environment variables.</p>
          )}
          {envVars && envVars.length > 0 && (
            <ul className="max-h-80 divide-y divide-border overflow-y-auto">
              {envVars.map((v) => (
                <li
                  key={v.key}
                  className="grid grid-cols-[minmax(0,2fr)_minmax(0,3fr)_2rem] items-center gap-2 px-3.5 py-1.5"
                >
                  <span className="truncate font-mono text-[12.5px] text-text" title={v.key}>
                    {v.key}
                  </span>
                  <span
                    className="truncate font-mono text-[12.5px] text-text-2"
                    title={visible.has(v.key) ? v.value : undefined}
                  >
                    {visible.has(v.key) ? v.value : '••••••••'}
                  </span>
                  <button
                    type="button"
                    onClick={() => toggleVisible(v.key)}
                    aria-label={visible.has(v.key) ? `Hide value of ${v.key}` : `Show value of ${v.key}`}
                    className="justify-self-end rounded p-1 text-text-3 transition-colors hover:text-text"
                  >
                    {visible.has(v.key) ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}
