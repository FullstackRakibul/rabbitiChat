// ম্যাট্রিক্স — how a summary or an answer is shown (docs/MATRIX_PROMPT.md §3.8, §3.9).
//
// The model's text, as HR shows its answers (hr-text.js): calculated numbers wrapped
// with their formula, numbers the check could not trace marked, labels that name
// nothing marked "unknown source" — and every citation ([C3] a card line, [R2] a
// result row, [KPI: Attrition %] a definition) made a chip the page opens.

import { renderMarkdown } from '@/lib/markdown.js'
import { attr, markUnknownSources, markUnverified, withFormulas } from '@/lib/hr-text.js'

const CITE = /\[(C\d+|R\d+|KPI:\s*[^\]<]{1,80})\]/g

/** Citations as chips: data-cite holds the label (C3, R2, KPI:…), which the page resolves. */
export function citeChips(html, known = () => true) {
  return String(html).split(/(<[^>]*>)/).map((part, i) => (i % 2 === 1 ? part : part.replace(CITE, (m, label) => {
    const l = label.replace(/\s+/g, ' ').trim()
    if (!known(l)) return m
    return `<button type="button" data-cite="${attr(l)}" class="mx-0.5 inline-flex items-center rounded border bg-muted px-1 align-baseline text-[11px] font-medium leading-4 text-muted-foreground hover:bg-accent hover:text-foreground">${l.startsWith('KPI:') ? 'KPI' : l}</button>`
  }))).join('')
}

/**
 * A checked text as HTML: formulas, markdown, unverified numbers, unknown labels,
 * citation chips. `known(label)` says whether a label names something sent.
 */
export function renderChecked(text, { formulas = [], unverified = [], unknown = [], known = () => true } = {}) {
  const html = renderMarkdown(withFormulas(text, formulas))
  return citeChips(markUnknownSources(markUnverified(html, unverified), unknown), (l) => !unknown.includes(l) && known(l))
}
