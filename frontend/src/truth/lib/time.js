// Times in Bangladesh time first (docs/TRUTH_PROMPT.md §1.1 rule 6). Everything
// is stored and sent as UTC; here it becomes Asia/Dhaka, whatever the visitor's
// own clock says, with Bangla digits in Bangla. The words — month names, the
// parts of the day, the patterns — are all site text (time.*).

const ZONE = 'Asia/Dhaka'
const BN = '০১২৩৪৫৬৭৮৯'

const parts = new Intl.DateTimeFormat('en-US', {
  timeZone: ZONE, hourCycle: 'h23', year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: 'numeric',
})

/** y, mo, d, h, mi of an instant, in Dhaka. */
export function dhaka(iso) {
  const out = {}
  for (const p of parts.formatToParts(new Date(iso))) if (p.type !== 'literal') out[p.type] = Number(p.value)
  return { y: out.year, mo: out.month, d: out.day, h: out.hour % 24, mi: out.minute }
}

export function digits(value, lang) {
  return lang === 'bn' ? String(value).replace(/\d/g, (d) => BN[d]) : String(value)
}

/** The Bangla part of the day for an hour, as the site text names it. */
function daypart(h) {
  if (h >= 4 && h < 6) return 'dawn'
  if (h >= 6 && h < 12) return 'morning'
  if (h >= 12 && h < 15) return 'noon'
  if (h >= 15 && h < 18) return 'afternoon'
  if (h >= 18 && h < 20) return 'evening'
  return 'night'
}

function dateText(t, lang, y, mo, d) {
  return t('time.date', { day: digits(d, lang), month: t(`time.m${mo}`), year: digits(y, lang) })
}

/** "২১ অক্টোবর ২০২৬" / "21 October 2026" — for a yyyy-MM-dd date, which has no time zone. */
export function formatDay(t, lang, ymd) {
  const [y, mo, d] = String(ymd).split('-').map(Number)
  return dateText(t, lang, y, mo, d)
}

export function formatDate(t, lang, iso) {
  const p = dhaka(iso)
  return dateText(t, lang, p.y, p.mo, p.d)
}

/** "রাত ৮টা" / "8 PM", with minutes when there are any. */
export function formatClock(t, lang, iso) {
  const { h, mi } = dhaka(iso)
  const h12 = h % 12 === 0 ? 12 : h % 12
  const values = {
    daypart: t(`time.${daypart(h)}`),
    hour: digits(h12, lang),
    minute: lang === 'bn' ? digits(mi, lang) : String(mi).padStart(2, '0'),
    ampm: t(h < 12 ? 'time.am' : 'time.pm'),
  }
  return t(mi === 0 ? 'time.clock' : 'time.clockMinutes', values)
}

export function formatDateTime(t, lang, iso) {
  return t('time.dateTime', { date: formatDate(t, lang, iso), time: formatClock(t, lang, iso) })
}

/** "২১ অক্টোবর ২০২৬, রাত ৮টা (14:00 UTC)" — rule 6's both-zones form, for a time a source gives in UTC. */
export function formatWithUtc(t, lang, iso) {
  const d = new Date(iso)
  const utc = `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}`
  return t('time.withUtc', { local: formatDateTime(t, lang, iso), utc })
}

export function formatRange(t, lang, start, end) {
  return t('time.range', { start: formatDay(t, lang, start), end: formatDay(t, lang, end) })
}
