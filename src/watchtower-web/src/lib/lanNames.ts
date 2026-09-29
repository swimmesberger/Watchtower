/**
 * Reading the LAN names field (ADR-0033). Two readers need the same answer and used to have their own:
 * the Routes page, which turns the names into the URLs a port route is reachable at, and the Settings
 * page, which asks whether a suggested name is already in the box.
 */

/**
 * The entries the field holds, exactly as the operator typed them — comma- or newline-separated,
 * trimmed, blanks dropped. The spelling is preserved because the Routes page builds URLs out of these,
 * and an address in a link has to be the one that was written.
 */
export function parseLanNames(raw: string | undefined): string[] {
  return (raw ?? '')
    .split(/[,\n\r]+/)
    .map(entry => entry.trim())
    .filter(Boolean)
}

/**
 * One entry reduced to the form two spellings of the same name share: the trailing dot of a fully
 * qualified name dropped, and lowercased. For comparing, never for displaying — `nas.lan.` and
 * `nas.lan` are one name, and a suggestion chip that did not know that would never disappear once it
 * was clicked.
 */
export function lanNameKey(entry: string): string {
  return entry.trim().replace(/\.$/, '').toLowerCase()
}
