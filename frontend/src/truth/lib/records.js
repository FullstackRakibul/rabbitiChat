// The Records Department on the public page (docs/TRUTH_PROMPT_P5.md §4): the live status strip and
// the wire. Loads go through lib/api.js, so when the backend is down the last copy is shown with its
// time. Filters and search run here, over the loaded rows — they never ask the server (G5.9) — and
// are remembered per browser (try/catch: storage may be blocked).

import { load } from './api.js'
import { digits } from './time.js'

export const STATE_TONE = { operational: 'green', incident: 'red', maintenance: 'amber', stale: 'grey' }
export const WIRE_STATE_TONE = { investigating: 'red', identified: 'red', monitoring: 'amber', maintenance: 'amber', scheduled: 'grey', resolved: 'green' }
export const SEVERITY_TONE = { critical: 'red', high: 'red', medium: 'amber', low: 'grey', info: 'grey' }
export const TYPES = ['status', 'advisory', 'release', 'news']
export const TIERS = ['official', 'authority', 'community', 'news']
export const SEVERITIES = ['critical', 'high', 'medium', 'low']

/** The site-text key of a tier ("news" is wire.newsTier, so it doesn't collide with the type). */
export const tierKey = (tier) => (tier === 'news' ? 'wire.newsTier' : `wire.${tier}`)
/** The site-text key of an incident state ("maintenance" is wire.maintenanceState). */
export const stateKey = (state) => (state === 'maintenance' ? 'wire.maintenanceState' : `wire.${state}`)

export const loadStatus = () => load('/status')
export const loadWire = (cursor) => load(cursor ? `/wire?cursor=${encodeURIComponent(cursor)}` : '/wire')
export const loadCluster = (id) => load(`/wire/${id}`)

/** "3 min" / "২ ঘণ্টা": how long ago, by the server's clock. */
export function ago(t, lang, iso, now) {
  const mins = Math.max(0, Math.floor((now - Date.parse(iso)) / 60_000))
  if (mins < 1) return t('ago.now')
  if (mins < 60) return t('ago.minutes', { count: digits(mins, lang) })
  if (mins < 48 * 60) return t('ago.hours', { count: digits(Math.floor(mins / 60), lang) })
  return t('ago.days', { count: digits(Math.floor(mins / 1440), lang) })
}

const FILTERS = 'truth-wire-filters'
const SEEN = 'truth-wire-seen'
export const EMPTY_FILTERS = { tech: '', type: '', severity: '', tier: '', open: false }

export function readFilters() {
  try { return { ...EMPTY_FILTERS, ...JSON.parse(localStorage.getItem(FILTERS) ?? '{}') } } catch { return { ...EMPTY_FILTERS } }
}
export function saveFilters(f) {
  try { localStorage.setItem(FILTERS, JSON.stringify(f)) } catch { /* storage blocked */ }
}

/** The rows this browser has already shown; null on a first visit (then nothing counts as new). */
export function readSeen() {
  try { const raw = localStorage.getItem(SEEN); return raw ? new Set(JSON.parse(raw)) : null } catch { return null }
}
export function saveSeen(ids) {
  try { localStorage.setItem(SEEN, JSON.stringify([...ids].slice(-500))) } catch { /* storage blocked */ }
}

/** The rows a visitor's filters and search keep. Plain text matching, in the browser. */
export function filterRows(rows, f, query) {
  const q = query.trim().toLowerCase()
  return rows.filter((r) => (!f.tech || r.techs.includes(f.tech))
    && (!f.type || r.kind === f.type)
    && (!f.severity || r.severity === f.severity)
    && (!f.tier || r.source.tier === f.tier)
    && (!f.open || ['investigating', 'identified', 'monitoring', 'maintenance'].includes(r.state))
    && (!q || [r.title, r.source.name, ...r.cves, ...r.techs].some((s) => s.toLowerCase().includes(q))))
}

/** Rows by Bangladesh day, newest day first: [{ day: 'YYYY-MM-DD', rows }]. */
export function byDay(rows) {
  const days = new Map()
  for (const r of rows) {
    const d = new Date(Date.parse(r.at) + 6 * 3600_000).toISOString().slice(0, 10)
    if (!days.has(d)) days.set(d, [])
    days.get(d).push(r)
  }
  return [...days].map(([day, list]) => ({ day, rows: list }))
}
