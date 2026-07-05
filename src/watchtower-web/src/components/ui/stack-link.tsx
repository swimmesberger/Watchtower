import { Link } from '@tanstack/react-router'
import { ExternalLink } from 'lucide-react'

// A link from a fleet-wide row back to a stack's specific detail tab (F9 deep-link), or a plain label
// when the compose project can't be resolved to a stack Watchtower manages. Shared by the volumes and
// networks sections on the Infrastructure page.
export function StackLink({
  project,
  stackIdByProject,
  tab,
}: {
  project: string | null
  stackIdByProject: Map<string, number>
  tab: 'volumes' | 'networks'
}) {
  if (!project) return <span className="text-text-3">—</span>
  const id = stackIdByProject.get(project)
  if (id == null) {
    return <span className="font-mono text-[13px] text-text-2">{project}</span>
  }
  return (
    <Link
      to="/stacks/$id"
      params={{ id: String(id) }}
      search={{ tab }}
      className="inline-flex items-center gap-1 font-medium text-text hover:text-brand"
    >
      {project}
      <ExternalLink className="size-3.5 shrink-0 text-text-3" aria-hidden />
    </Link>
  )
}
