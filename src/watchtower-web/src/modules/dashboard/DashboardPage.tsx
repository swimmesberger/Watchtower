// The dashboard is a SECTION HOST: it renders the page header + the ordered `dashboardSections`
// contributions (this module's own sections interleaved with the sibling metrics module's
// host-health strip and resource-usage ranking). It owns no data or section markup itself —
// each section is self-contained (its own queries, loading, empty, and error states).
import { Link } from '@tanstack/react-router'
import { Plus } from 'lucide-react'
import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { dashboardSections } from '@/platform/points'
import { LiveChip } from './sections'

export function DashboardPage() {
  const sections = useContributions(dashboardSections)

  return (
    <div className="space-y-6">
      {/* Page header */}
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2.5">
          <h1 className="text-2xl font-semibold tracking-tight text-text">Dashboard</h1>
          <LiveChip />
        </div>
        {/* Desktop primary action; on mobile this becomes the FAB below. */}
        <Button asChild variant="primary" className="hidden md:inline-flex">
          <Link to="/stacks/new">
            <Plus /> New stack
          </Link>
        </Button>
      </div>

      {/* Ordered dashboard sections — this module's own + the sibling metrics module's. Each
          section fetches its own data and renders its own loading/empty/error states. */}
      {sections.map((section) => (
        <section.component key={section.id} />
      ))}

      {/* Mobile FAB → New stack (sticky above the bottom tab bar). */}
      <Link
        to="/stacks/new"
        aria-label="New stack"
        className={cn(
          'fixed bottom-[calc(var(--bottombar-h)+env(safe-area-inset-bottom)+16px)] right-4 z-20',
          'flex size-14 items-center justify-center rounded-full bg-brand text-brand-fg shadow-[var(--sh-lg)]',
          'transition-colors hover:bg-[var(--brand-hover)] active:translate-y-px',
          'focus-visible:outline-none focus-visible:shadow-[var(--sh-focus)]',
          'md:hidden',
        )}
      >
        <Plus className="size-6" />
      </Link>
    </div>
  )
}
