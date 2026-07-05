import { cn } from '@/lib/utils'
import { Skeleton } from './skeleton'

export interface DataListColumn<T> {
  /** Stable id for the column (also the React key). */
  key: string
  /** Header cell content. */
  header: React.ReactNode
  /** Cell renderer for a row. */
  cell: (item: T) => React.ReactNode
  /** Extra className on both header and body cells (e.g. width, alignment). */
  className?: string
  /** Right-align (for action columns). */
  align?: 'left' | 'right'
}

export interface DataListProps<T> {
  items: T[]
  columns: DataListColumn<T>[]
  getKey: (item: T) => string | number
  /** Renders one item as a card at <768px. Falls back to a generic layout if omitted. */
  renderCard?: (item: T) => React.ReactNode
  /** When set, renders N skeleton rows (desktop) / 3 skeleton cards (mobile) instead of data. */
  skeletonRows?: number
  /** Shown when items is empty and not loading. */
  emptyState?: React.ReactNode
  /** Optional row click → navigate handler (whole row/card tappable). */
  onRowClick?: (item: T) => void
  className?: string
  /** Aria label for the table. */
  'aria-label'?: string
}

/**
 * THE responsive list primitive. Semantic <table> at md+ (sticky zebra header,
 * 44px rows); stacked cards below md. Provide `renderCard` for the mobile layout.
 */
export function DataList<T>({
  items,
  columns,
  getKey,
  renderCard,
  skeletonRows,
  emptyState,
  onRowClick,
  className,
  'aria-label': ariaLabel,
}: DataListProps<T>) {
  const loading = skeletonRows != null && skeletonRows > 0
  const isEmpty = !loading && items.length === 0

  if (isEmpty && emptyState) {
    return <>{emptyState}</>
  }

  return (
    <div className={className}>
      {/* Desktop: table */}
      <div className="hidden overflow-x-auto rounded-lg border border-border md:block">
        <table className="w-full border-collapse text-sm" aria-label={ariaLabel}>
          <thead>
            <tr className="sticky top-0 z-10 bg-surface-2">
              {columns.map((c) => (
                <th
                  key={c.key}
                  className={cn(
                    'whitespace-nowrap px-4 py-2.5 text-left text-xs font-medium uppercase tracking-[0.04em] text-text-3',
                    c.align === 'right' && 'text-right',
                    c.className,
                  )}
                >
                  {c.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {loading
              ? Array.from({ length: skeletonRows! }).map((_, i) => (
                  <tr key={`sk-${i}`} className="border-t border-border">
                    {columns.map((c) => (
                      <td key={c.key} className="px-4 py-3">
                        <Skeleton variant="line" className="w-2/3" />
                      </td>
                    ))}
                  </tr>
                ))
              : items.map((item) => (
                  <tr
                    key={getKey(item)}
                    onClick={onRowClick ? () => onRowClick(item) : undefined}
                    className={cn(
                      'border-t border-border transition-colors',
                      onRowClick && 'cursor-pointer hover:bg-surface-2',
                    )}
                  >
                    {columns.map((c) => (
                      <td
                        key={c.key}
                        className={cn(
                          'h-11 px-4 align-middle',
                          c.align === 'right' && 'text-right',
                          c.className,
                        )}
                      >
                        {c.cell(item)}
                      </td>
                    ))}
                  </tr>
                ))}
          </tbody>
        </table>
      </div>

      {/* Mobile: stacked cards */}
      <div className="flex flex-col gap-3 md:hidden">
        {loading
          ? Array.from({ length: 3 }).map((_, i) => (
              <div key={`skc-${i}`} className="rounded-lg border border-border bg-surface p-4">
                <Skeleton variant="line" className="mb-2 w-1/2" />
                <Skeleton variant="line" className="w-3/4" />
              </div>
            ))
          : items.map((item) => (
              <div
                key={getKey(item)}
                onClick={onRowClick ? () => onRowClick(item) : undefined}
                className={cn(
                  'rounded-lg border border-border bg-surface p-4',
                  onRowClick && 'cursor-pointer',
                )}
              >
                {renderCard ? (
                  renderCard(item)
                ) : (
                  columns.map((c) => (
                    <div key={c.key} className="flex justify-between gap-3 py-0.5 text-sm">
                      <span className="text-text-3">{c.header}</span>
                      <span className="min-w-0 truncate text-right">{c.cell(item)}</span>
                    </div>
                  ))
                )}
              </div>
            ))}
      </div>
    </div>
  )
}
