// Small helpers of the Ministry of Truth's admin tab (components/truth/*).

/** "23 Oct 2026, 20:00 (Dhaka)" — staff see times in Bangladesh time too (rule 6). */
export function formatDhaka(iso) {
  if (!iso) return ''
  return new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Dhaka', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).format(new Date(iso)) + ' (Dhaka)'
}

/** "2026-w43" for the ISO week a date falls in. */
export function isoWeekSlug(ymd) {
  const d = new Date(`${ymd}T00:00:00Z`)
  const day = (d.getUTCDay() + 6) % 7
  d.setUTCDate(d.getUTCDate() - day + 3)
  const year = d.getUTCFullYear()
  const first = new Date(Date.UTC(year, 0, 4))
  const week = 1 + Math.round(((d - first) / 86400000 - 3 + ((first.getUTCDay() + 6) % 7)) / 7)
  return `${year}-w${String(week).padStart(2, '0')}`
}

/** A UTC ISO time as the value of an <input type="datetime-local"> read as UTC, and back. */
export const toUtcInput = (iso) => (iso ? iso.slice(0, 16) : '')
export const fromUtcInput = (v) => (v ? `${v}:00Z` : null)

export const STATUS_BADGE = { draft: 'outline', review: 'secondary', published: 'default' }

export const STATUSES = ['active_attack', 'breach_confirmed', 'patch_scheduled', 'ongoing', 'contained', 'unconfirmed', 'patched', 'blocked', 'resolved']
export const SEVERITIES = ['critical', 'high', 'medium', 'low', 'info']
export const LEVELS = ['ERR', 'WARN', 'FIX', 'INFO']
export const SOURCE_KINDS = ['official', 'vendor', 'research', 'community', 'news', 'claim']
export const CATEGORIES = ['language', 'runtime', 'platform', 'ai-provider', 'ca', 'database', 'app']

/** The colour of a status, as the public pill draws it (its family; the label is site text there). */
export function statusTone(status) {
  if (['active_attack', 'breach_confirmed'].includes(status)) return 'text-red-600 dark:text-red-400'
  if (['patch_scheduled', 'ongoing', 'contained'].includes(status)) return 'text-amber-600 dark:text-amber-400'
  if (status === 'unconfirmed') return 'text-muted-foreground'
  return 'text-green-600 dark:text-green-400'
}
