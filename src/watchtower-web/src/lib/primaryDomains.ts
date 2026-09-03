/**
 * Splitting and composing a hostname against the primary domains routes live under (ADR-0036). Display
 * and prefill only: what is a legal hostname, which zone it belongs to and whether it may be created at
 * all are the server's questions, and `DesiredHosts.TryNormalize` in `proxy.createRoute` stays the only
 * gate. Nothing here rejects anything — a value these functions cannot decompose is simply typed in full.
 *
 * The comparisons are deliberately plain: `toLowerCase()` rather than `toLocaleLowerCase()` (a Turkish
 * locale would fold `I` to `ı` and stop matching a domain the server considers the same one) and no
 * Unicode normalisation (hostnames arrive punycoded, and re-composing them here would only invent
 * spellings the server never sends).
 */

/** One host in the form two spellings of the same name share: trimmed, one trailing dot dropped, lowercased. */
export function normalizeHost(host: string): string {
  return host.trim().replace(/\.$/, '').toLowerCase()
}

/**
 * Whether `host` lives under `primary` — the apex itself counts. The boundary is a whole label, so
 * `example.com` covers `app.example.com` but not `notexample.com`.
 */
export function coversHost(primary: string, host: string): boolean {
  const base = normalizeHost(primary)
  const name = normalizeHost(host)
  if (base === '' || name === '') return false
  return name === base || name.endsWith(`.${base}`)
}

/**
 * The primary domain a host belongs to: the longest one that covers it, so `dev.example.com` wins over
 * `example.com` for `app.dev.example.com`. Ties break on the name itself, which keeps the answer
 * independent of the order the list arrived in. Null when nothing covers the host.
 */
export function bestPrimaryDomain(primaries: string[], host: string): string | null {
  let best: string | null = null
  for (const primary of primaries) {
    if (!coversHost(primary, host)) continue
    if (
      best === null ||
      primary.length > best.length ||
      (primary.length === best.length && primary < best)
    )
      best = primary
  }
  return best
}

/**
 * The part of `host` in front of `primary`: `''` when the host *is* the apex, null when the primary
 * does not cover it at all. The two are different answers — an empty subdomain is a route on the apex.
 */
export function subdomainOf(primary: string, host: string): string | null {
  if (!coversHost(primary, host)) return null
  const base = normalizeHost(primary)
  const name = normalizeHost(host)
  return name === base ? '' : name.slice(0, name.length - base.length - 1)
}

/**
 * The hostname a subdomain and a primary domain spell together. An empty subdomain is the apex. Stray
 * whitespace and leading/trailing dots are dropped so `app.` and ` app ` compose the same host as `app`.
 */
export function composeHost(subdomain: string, primary: string): string {
  const sub = subdomain.trim().replace(/^\.+|\.+$/g, '')
  const base = primary.trim().replace(/^\.+|\.+$/g, '')
  return sub === '' ? base : `${sub}.${base}`
}

/**
 * A typed hostname taken apart into the two halves the composed control edits, or null when no primary
 * domain covers it — which is exactly the case the custom-hostname field exists for.
 */
export function splitHost(
  primaries: string[],
  host: string,
): { subdomain: string; primaryDomain: string } | null {
  const primary = bestPrimaryDomain(primaries, host)
  if (primary === null) return null
  return { subdomain: subdomainOf(primary, host) ?? '', primaryDomain: primary }
}
