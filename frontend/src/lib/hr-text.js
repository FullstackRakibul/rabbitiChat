// মানব সম্পদ — how HR text is shown: shared by Ask (answers) and the Rulebook
// (pages), so a worked-out amount looks and behaves the same in both.

export const escapeHtml = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

/** For a title attribute: escaped, and without brackets the citation pass would read as a citation. */
export const attr = (s) => escapeHtml(s).replace(/\[/g, '&#91;').replace(/\]/g, '&#93;')

/**
 * Each amount the server calculated, wrapped where it sits (the server sends its
 * offset), so hovering or tapping it shows the formula. Inserted from the end, so
 * the earlier offsets stay valid.
 */
export function withFormulas(content, formulas) {
  let text = content || ''
  const list = (formulas ?? []).map((f, i) => ({ ...f, i }))
    .filter((f) => f.offset >= 0 && f.offset + f.length <= text.length)
    .sort((a, b) => b.offset - a.offset)
  for (const f of list) {
    const title = f.ok ? `= ${f.expression}` : `${f.expression} — ${f.error ?? ''}`
    const cls = f.ok
      ? 'cursor-help rounded-sm bg-primary/10 px-0.5 font-medium underline decoration-dotted underline-offset-2'
      : 'cursor-help rounded-sm bg-destructive/10 px-0.5 text-destructive dark:text-foreground'
    text = `${text.slice(0, f.offset)}<span data-formula="${f.i}" title="${attr(title)}" class="${cls}">${escapeHtml(text.slice(f.offset, f.offset + f.length))}</span>${text.slice(f.offset + f.length)}`
  }
  return text
}

// An amount as the server's number check reads one: 12,000 and 1,23,456 whole, or a
// plain number; Bengali digits count. (HrNumberCheck.Amount, for highlighting only.)
const AMOUNT = /(?<![\d.,০-৯])[\d০-৯]{1,3}(?:,[\d০-৯]{2,3})+(?:\.\d+)?(?![\d০-৯])|(?<![\d.,০-৯])[\d০-৯]+(?:\.\d+)?(?![\d০-৯])/g
const toNumber = (s) => Number(s.replace(/[০-৯]/g, (c) => String(c.charCodeAt(0) - 0x09e6)).replace(/,/g, ''))

/**
 * The amounts the number check could not trace, marked where they appear
 * (data-unverified). Only text between tags is touched, so a formula's title or a
 * citation's attributes stay as they are.
 */
export function markUnverified(html, amounts) {
  if (!amounts?.length) return html
  const want = new Set(amounts.map(Number))
  return String(html).split(/(<[^>]*>)/).map((part, i) => (i % 2 === 1 ? part : part.replace(AMOUNT, (m) => (want.has(toNumber(m))
    ? `<mark data-unverified title="Not found in the sources this page was drafted from" class="rounded-sm bg-destructive/15 px-0.5 text-foreground underline decoration-destructive decoration-wavy underline-offset-2">${m}</mark>`
    : m)))).join('')
}

export const UNKNOWN_SOURCE = 'অজানা উৎস · unknown source'

/**
 * Citations that name no place that was sent (the server's HrCitations), marked
 * where they appear as "unknown source" (data-unknown-source), the way an
 * unverified amount is. Only text between tags is touched.
 */
export function markUnknownSources(html, labels) {
  if (!labels?.length) return html
  return String(html).split(/(<[^>]*>)/).map((part, i) => (i % 2 === 1 ? part : labels.reduce((text, label) => {
    const plain = `[${escapeHtml(label)}]`
    return text.split(plain).join(`<mark data-unknown-source title="${attr(UNKNOWN_SOURCE)}" class="rounded-sm bg-destructive/15 px-0.5 text-foreground underline decoration-destructive decoration-wavy underline-offset-2">${plain}</mark>`)
  }, part))).join('')
}

/** ৳ with the separators HR reads (৳77,777; ৳1,20,000) — for listing an amount, as HrFormulas.Taka shows one. */
export function taka(n) {
  return `৳${Number(n).toLocaleString('en-IN')}`
}
