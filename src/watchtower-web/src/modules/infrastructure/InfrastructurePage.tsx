import { useContributions } from '@swimmesberger/elarion-contributions/react'
import { infraSections } from '@/platform/points'

// UI-only section host: the fleet-wide Infrastructure page owns nothing but its header. Each section
// (Exposure, Volumes, Networks) is a self-contained contribution to `infraSections` from the volumes
// and networks modules, rendered here in contribution order.
export function InfrastructurePage() {
  const sections = useContributions(infraSections)

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-8 px-4 py-6 md:px-6">
      <div>
        <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Infrastructure</h1>
        {/* F10: one plain-language sentence under the header. */}
        <p className="mt-1 text-sm text-text-2">
          Everything Docker holds on this host that isn&apos;t tied to a single stack view —
          storage, networks, and exposure across all stacks.
        </p>
      </div>

      {sections.map((s) => (
        <s.component key={s.id} />
      ))}
    </div>
  )
}
