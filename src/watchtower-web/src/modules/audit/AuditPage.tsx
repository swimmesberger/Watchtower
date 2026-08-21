import { AuditTrailCard } from '@/components/audit-trail'

/**
 * The global audit trail: every category, newest first. Scoped views live where their plane lives
 * (Routes → Audit embeds the `proxy` slice); this page is where a new category shows up without any
 * frontend change — the backend recording under a new category is the whole integration.
 */
export function AuditPage() {
  return (
    <div className="mx-auto max-w-[1000px] space-y-6 p-4 md:p-6">
      <header>
        <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Audit</h1>
        <p className="mt-1 text-[13px] text-text-2">
          What Watchtower changed — writes against external control planes (Cloudflare), backup runs,
          restores and retention prunes, more planes as they start recording. Reads are never logged.
        </p>
      </header>
      <AuditTrailCard
        title="All events"
        description="Every recorded write across all categories, newest first."
        emptyText="Nothing recorded yet. Entries appear when Watchtower changes something — e.g. pushing tunnel configuration, DNS records or Access applications on the Cloudflare provider, or running a stack backup."
        showCategory
      />
    </div>
  )
}
