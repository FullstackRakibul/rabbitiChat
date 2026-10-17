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
