// Small date helpers shared by bubbles (timestamps) and history (day groups).

/** Parse a Date | ISO string; naive strings (no timezone) are assumed UTC. */
export function toDate(value) {
  if (!value) return null
  if (value instanceof Date) return value
  let s = String(value)
  if (!/([zZ]|[+-]\d{2}:?\d{2})$/.test(s)) s += 'Z'
  const d = new Date(s)
  return Number.isNaN(d.getTime()) ? null : d
}

/** "11:22" style clock time, or '' when the value is unusable. */
export function formatTime(value) {
  const d = toDate(value)
  return d
    ? d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hour12: false })
    : ''
}

/** Sidebar group label: Today / Yesterday / "Friday, March 26, 2026". */
export function dayLabel(value) {
  const d = toDate(value)
  if (!d) return 'Earlier'
  const startOfDay = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime()
  const days = Math.round((startOfDay(new Date()) - startOfDay(d)) / 86400000)
  if (days <= 0) return 'Today'
  if (days === 1) return 'Yesterday'
  return d.toLocaleDateString(undefined, {
    weekday: 'long', year: 'numeric', month: 'long', day: 'numeric',
  })
}
