import { Link, type LinkProps } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface StatCardProps {
  label: string
  value: React.ReactNode
  icon?: LucideIcon
  /** Left accent bar tone. */
  accent?: 'brand' | 'ok' | 'warn' | 'danger' | 'run' | 'neutral'
  /** Small leading dot next to the value (e.g. status color). */
  dotTone?: 'ok' | 'warn' | 'danger' | 'run' | 'queue' | 'neutral'
  /**
   * Makes the whole card a router link (A5). Accepts a TanStack Router link
   * target, e.g. to="/stacks" or { to: '/stacks', search: { status: 'ok' } }.
   */
  to?: LinkProps['to']
  search?: LinkProps['search']
  className?: string
}

const accentBar: Record<NonNullable<StatCardProps['accent']>, string> = {
  brand: 'bg-brand',
  ok: 'bg-ok',
  warn: 'bg-warn',
  danger: 'bg-danger',
  run: 'bg-run',
  neutral: 'bg-neutral',
}

const dotColor: Record<NonNullable<StatCardProps['dotTone']>, string> = {
  ok: 'bg-ok',
  warn: 'bg-warn',
  danger: 'bg-danger',
  run: 'bg-run',
  queue: 'bg-queue',
  neutral: 'bg-neutral',
}

function Body({ label, value, icon: Icon, accent, dotTone, linked }: StatCardProps & { linked: boolean }) {
  return (
    <>
      {accent && <span className={cn('absolute inset-y-0 left-0 w-[3px] rounded-l-lg', accentBar[accent])} aria-hidden />}
      <div className="flex items-start justify-between gap-2">
        <p className="text-xs font-medium uppercase tracking-[0.04em] text-text-3">{label}</p>
        {Icon && <Icon className="size-4 text-text-3" aria-hidden />}
        {linked && !Icon && (
          <ChevronRight className="size-4 text-text-3 opacity-0 transition-opacity group-hover:opacity-100" aria-hidden />
        )}
      </div>
      <div className="mt-2 flex items-center gap-2">
        {dotTone && <span className={cn('size-2 rounded-full', dotColor[dotTone])} aria-hidden />}
        <span className="tnum text-[26px] font-semibold leading-none tracking-tight text-text">{value}</span>
      </div>
    </>
  )
}

/** Compact metric card. Pass `to` to make the whole card a navigable link (A5). */
export function StatCard(props: StatCardProps) {
  const { to, search, className } = props
  const base =
    'group relative flex flex-col rounded-lg border border-border bg-surface p-4 text-left'

  if (to) {
    return (
      <Link
        to={to}
        search={search as never}
        className={cn(base, 'transition-colors hover:border-border-strong hover:shadow-[var(--sh-sm)]', className)}
      >
        <Body {...props} linked />
      </Link>
    )
  }
  return (
    <div className={cn(base, className)}>
      <Body {...props} linked={false} />
    </div>
  )
}
