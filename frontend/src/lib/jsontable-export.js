/**
 * Export helpers for the JSON → table module.
 *
 * The HTML export is built from the row model rather than scraped from the live
 * DOM: the rendered table may be paginated, and lifting innerHTML would export
 * only the visible page plus a payload of Tailwind classes and Vue attributes.
 * Generating it means the file is self-contained, styled inline, and contains
 * every row — with the rowspans that make it a report.
 */
import { toText } from './jsontable.js'

const escapeHtml = (v) => toText(v)
  .replace(/&/g, '&amp;')
  .replace(/</g, '&lt;')
  .replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;')

/**
 * A standalone HTML document of the merged (report) table.
 *
 * Styles are inline in a <style> block so the file opens correctly anywhere —
 * a browser, an email, or Excel via "Open as web page", which is the usual way
 * a merged table gets into a spreadsheet.
 *
 * @param {Array} rows      row model (already filtered/sorted)
 * @param {object} schema   visible columns: { scalars, groups }
 * @param {string} title
 */
export function toHtmlTable(rows, schema, title = 'JSON Table') {
  const groupColumns = schema.groups.flatMap((g) => g.columns)

  const head = []
  head.push('<tr>')
  if (schema.scalars.length) {
    head.push(`<th colspan="${schema.scalars.length}" class="grp">Record</th>`)
  }
  for (const g of schema.groups) {
    head.push(`<th colspan="${g.columns.length}" class="grp">${escapeHtml(g.label)}</th>`)
  }
  head.push('</tr><tr>')
  for (const c of schema.scalars) head.push(`<th>${escapeHtml(c.label)}</th>`)
  for (const c of groupColumns) head.push(`<th>${escapeHtml(c.label)}</th>`)
  head.push('</tr>')

  const body = []
  for (const row of rows) {
    for (let i = 0; i < row.rowHeight; i++) {
      body.push(i === 0 ? '<tr class="rec">' : '<tr>')

      // The merge itself: scalar cells are emitted once, on the record's first
      // row, spanning its full height.
      if (i === 0) {
        for (const c of schema.scalars) {
          const span = row.rowHeight > 1 ? ` rowspan="${row.rowHeight}"` : ''
          body.push(`<td${span}>${escapeHtml(row.scalars[c.path])}</td>`)
        }
      }

      for (const c of groupColumns) {
        const item = (row.groups[c.group] || [])[i]
        body.push(`<td>${item ? escapeHtml(item[c.path]) : ''}</td>`)
      }

      body.push('</tr>')
    }
  }

  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>${escapeHtml(title)}</title>
<style>
  body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; margin: 24px; color: #111; }
  h1 { font-size: 16px; margin: 0 0 12px; }
  table { border-collapse: collapse; font-size: 12px; }
  th, td { border: 1px solid #999; padding: 4px 8px; text-align: left; vertical-align: top; }
  th { background: #eee; font-weight: 600; white-space: nowrap; }
  th.grp { background: #ddd; text-align: center; }
  tr.rec > td { border-top: 2px solid #666; }
</style>
</head>
<body>
<h1>${escapeHtml(title)}</h1>
<table>
<thead>${head.join('')}</thead>
<tbody>${body.join('')}</tbody>
</table>
</body>
</html>`
}

/** Triggers a download of `text` as `filename`, then releases the object URL. */
export function downloadText(filename, text, mime = 'text/plain;charset=utf-8') {
  const blob = new Blob([text], { type: mime })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}

/** Clipboard write with a boolean result, so the caller can report success honestly. */
export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    return false
  }
}
