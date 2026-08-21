import { apiBase } from '@/lib/config'

/**
 * Triggers a browser download of a volume as a gzipped tar via the streaming endpoint
 * (`GET /api/volumes/{name}/download`, ADR-0016). A plain navigation (not fetch): the response is
 * `Content-Disposition: attachment`, so the browser streams it to a file, and the session cookie
 * rides along on its own.
 */
export function downloadVolumeArchive(name: string) {
  const a = document.createElement('a')
  a.href = `${apiBase}/api/volumes/${encodeURIComponent(name)}/download`
  a.download = ''
  document.body.appendChild(a)
  a.click()
  a.remove()
}
