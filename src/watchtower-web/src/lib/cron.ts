// ── Cron preview (ADR-0018) ──────────────────────────────────────────────────
// Mirrors the server's describer (`BackupSchedule.Describe`) field for field. The server's wording
// is what lands in the audit trail, so the phrases here must stay identical — "every 6 hours at :00",
// "on weekdays at 02:00", "on Mon, Wed and Fri at 02:00". Shapes the describer doesn't recognise
// return null; the caller then shows the raw expression instead of guessing.

const MONTH_NAMES = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC']

const DAY_NAMES = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT']

const DAY_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/**
 * Puts a five-field expression (`minute hour day-of-month month day-of-week`, server-local wall
 * clock) into words, e.g. `every day at 03:30 and 15:30`. Returns null when the expression is
 * unparsable or has a shape that reads badly in prose (a restricted month, or both day fields
 * restricted — Unix' "either matches" semantics).
 */
export function describeCron(expression: string): string | null {
  const raw = expression.trim()
  const fields = raw.split(/\s+/).filter(f => f.length > 0)
  if (fields.length !== 5) return null
  // The defaults only satisfy the indexed-access check — five non-empty fields exist by now.
  const [minuteField = '', hourField = '', domField = '', monthField = '', dowField = ''] = fields

  const minutes = expandField(minuteField, 0, 59, null)
  const hours = expandField(hourField, 0, 23, null)
  const daysOfMonth = expandField(domField, 1, 31, null)
  const months = expandField(monthField, 1, 12, MONTH_NAMES)
  const daysOfWeek = expandField(dowField, 0, 7, DAY_NAMES)
  if (!minutes || !hours || !daysOfMonth || !months || !daysOfWeek) return null
  if (daysOfWeek.delete(7)) daysOfWeek.add(0) // 7 is Sunday too

  const anyDom = isWildcard(domField)
  const anyMonth = isWildcard(monthField)
  const anyDow = isWildcard(dowField)
  if (!anyMonth || (!anyDom && !anyDow)) return null

  let when: string
  if (anyDom && anyDow) {
    when = 'every day'
  } else if (!anyDow) {
    const days = describeDaysOfWeek(daysOfWeek)
    if (days === null) return null
    when = days
  } else {
    when = `on day ${joinList(ascending(daysOfMonth).map(String))} of every month`
  }

  const time = describeTimes(minutes, hours, minuteField, hourField)
  if (time === null) return null
  // "every day every 6 hours" / "every day 9 times a day" say nothing the time part does not.
  const timeImpliesEveryDay = time.startsWith('every ') || time.endsWith(' a day')
  return when === 'every day' && timeImpliesEveryDay ? time : `${when} ${time}`
}

function describeTimes(
  minutes: Set<number>,
  hours: Set<number>,
  minuteField: string,
  hourField: string,
): string | null {
  const allMinutes = minutes.size === 60
  const allHours = hours.size === 24
  if (allMinutes && allHours) return 'every minute'
  if (allMinutes) return null
  if (minutes.size === 1) {
    const minute = ascending(minutes)[0] ?? 0
    if (allHours) return `every hour at :${pad2(minute)}`
    const hourStep = stepOf(hourField, 0)
    if (hourStep !== null && hourStep > 1) return `every ${hourStep} hours at :${pad2(minute)}`
  }
  const minuteStep = stepOf(minuteField, 0)
  if (minuteStep !== null && minuteStep > 1 && allHours) return `every ${minuteStep} minutes`

  const times = ascending(hours).flatMap(h => ascending(minutes).map(m => `${pad2(h)}:${pad2(m)}`))
  if (times.length > 8) return `${times.length} times a day`
  return `at ${joinList(times)}`
}

function describeDaysOfWeek(days: Set<number>): string | null {
  if (days.size === 0) return null
  if (setEquals(days, [1, 2, 3, 4, 5])) return 'on weekdays'
  if (setEquals(days, [0, 6])) return 'on weekends'
  if (days.size === 7) return 'every day'
  return `on ${joinList(ascending(days).map(d => DAY_LABELS[d] ?? String(d)))}`
}

/** "a", "a and b", "a, b and c". */
function joinList(items: string[]): string {
  if (items.length === 0) return ''
  if (items.length === 1) return items[0] ?? ''
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`
}

const isWildcard = (field: string) => field === '*' || field === '?'

const pad2 = (value: number) => String(value).padStart(2, '0')

const ascending = (values: Set<number>) => [...values].sort((a, b) => a - b)

const setEquals = (values: Set<number>, expected: number[]) =>
  values.size === expected.length && expected.every(v => values.has(v))

/** Digits only — no signs, no whitespace, matching the server's `NumberStyles.None`. */
function toInt(text: string): number | null {
  return /^\d+$/.test(text) ? Number(text) : null
}

// The step of a `*` or `min-…` range divided by n — i.e. a step starting at the field's minimum;
// null for anything else.
function stepOf(field: string, min: number): number | null {
  const slash = field.indexOf('/')
  if (slash < 0) return null
  const range = field.slice(0, slash)
  if (!(range === '*' || range === String(min) || range.startsWith(`${min}-`))) return null
  const step = toInt(field.slice(slash + 1))
  return step !== null && step > 0 ? step : null
}

/**
 * The value set of one field: wildcards, lists, ranges, steps and (where given) names. Only enough
 * cron to put a schedule into words — the server is the authority on validity.
 */
function expandField(field: string, min: number, max: number, names: string[] | null): Set<number> | null {
  const values = new Set<number>()
  for (const part of field.split(',')) {
    if (part.length === 0) return null
    let step = 1
    let range = part
    const slash = part.indexOf('/')
    if (slash >= 0) {
      const parsed = toInt(part.slice(slash + 1))
      if (parsed === null || parsed <= 0) return null
      step = parsed
      range = part.slice(0, slash)
    }
    let start: number
    let end: number
    if (isWildcard(range)) {
      start = min
      end = max
    } else {
      const dash = range.indexOf('-')
      if (dash >= 0) {
        const from = fieldValue(range.slice(0, dash), min, max, names)
        const to = fieldValue(range.slice(dash + 1), min, max, names)
        if (from === null || to === null) return null
        start = from
        end = to
      } else {
        const only = fieldValue(range, min, max, names)
        if (only === null) return null
        start = only
        end = slash >= 0 ? max : only
      }
    }
    if (start > end) return null
    for (let v = start; v <= end; v += step) values.add(v)
  }
  return values.size > 0 ? values : null
}

function fieldValue(text: string, min: number, max: number, names: string[] | null): number | null {
  if (names !== null) {
    const index = names.findIndex(n => n.toLowerCase() === text.toLowerCase())
    if (index >= 0) return min + index
  }
  const value = toInt(text)
  return value !== null && value >= min && value <= max ? value : null
}
