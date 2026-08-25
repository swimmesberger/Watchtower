// Helpers over a product's git source. Shared by the products, stacks and templates modules — the
// same repository URL is typed on three forms, and each of them offers to name the thing after it.

/**
 * The repository URL's last path segment: `https://github.com/acme/web.git` → `web`. Returns an empty
 * string when there is nothing usable, so callers can leave their field alone rather than filling it
 * with a placeholder.
 *
 * The frontend copy of `ProductSourceKey.DeriveName` — a suggestion for a form field, never the value
 * anything is keyed on. The backend derives its own name when it find-or-creates a product; this only
 * has to agree well enough that the suggested name is not a surprise.
 */
export function deriveProductName(repositoryUrl: string): string {
  const trimmed = repositoryUrl.trim().replace(/\/+$/, '')
  const withoutGit = trimmed.replace(/\.git$/i, '').replace(/\/+$/, '')
  // Everything up to the last '/' or ':' — the scp-like `git@host:owner/repo` form ends on a colon.
  const segment = withoutGit.replace(/^.*[/:]/, '').trim()
  return segment
}
