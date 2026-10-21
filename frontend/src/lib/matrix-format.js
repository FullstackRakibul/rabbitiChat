// ম্যাট্রিক্স — how numbers and dates read (docs/MATRIX_PROMPT.md §3.1).
//
// Lakh grouping (12,34,567) as Bangladesh writes numbers, Bengali digits when
// the label is Bengali, and dates as 19 May 2026. Formatting only — every
// number shown was computed by the server.

const BN_DIGITS = ['০', '১', '২', '৩', '৪', '৫', '৬', '৭', '৮', '৯']

/** "1234567" → "১২৩৪৫৬৭". */
export function bnDigits(text) {
  return String(text).replace(/\d/g, (d) => BN_DIGITS[Number(d)])
}

/** 1234567.5 → "12,34,567.5": the last three digits, then groups of two. */
export function lakh(value, { decimals } = {}) {
  if (value === null || value === undefined || value === '') return ''
  const n = Number(value)
  if (!Number.isFinite(n)) return String(value)
  const fixed = decimals === undefined ? String(Math.abs(n)) : Math.abs(n).toFixed(decimals)
  const [whole, frac] = fixed.split('.')
  const last3 = whole.slice(-3)
  const rest = whole.slice(0, -3).replace(/\B(?=(\d{2})+(?!\d))/g, ',')
  return `${n < 0 ? '-' : ''}${rest ? rest + ',' : ''}${last3}${frac ? '.' + frac : ''}`
}

/** A number in the user's script: lakh grouping, Bengali digits when asked. */
export function formatNumber(value, { bn = false, decimals } = {}) {
  const s = lakh(value, { decimals })
  return bn ? bnDigits(s) : s
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "2026-05-19" → "19 May 2026"; anything else as it came. */
export function formatDate(iso) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(iso ?? ''))
  return m ? `${Number(m[3])} ${MONTHS[Number(m[2]) - 1]} ${m[1]}` : (iso ?? '')
}

/** A cell for the viewer, by the column's type. Masked values ("•••") pass through. */
export function formatCell(value, type) {
  if (value === null || value === undefined) return ''
  if (value === '•••') return value
  if (type === 'date') return formatDate(value)
  if (type === 'real') return lakh(Math.round(Number(value) * 100) / 100)
  if (type === 'integer') return lakh(value)
  return String(value)
}

/** "10347259" → "9.9 MB". */
export function formatBytes(n) {
  if (!n && n !== 0) return ''
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(1)} MB`
}

/** A value as its unit reads: 12,34,567 people; 5.92%; 1.35 (a ratio); ৳ 12,000. Text passes through. */
export function formatValue(v, unit) {
  if (v === null || v === undefined) return '—'
  if (typeof v === 'string') return v
  if (unit === '%') return `${lakh(Math.round(v * 100) / 100)}%`
  if (unit === 'ratio') return Number(v).toFixed(2)
  if (unit === 'hours') return `${lakh(Math.round(v * 100) / 100)} h`
  if (unit === 'tk') return `৳ ${lakh(Math.round(v))}`
  return lakh(Math.round(v * 100) / 100)
}
