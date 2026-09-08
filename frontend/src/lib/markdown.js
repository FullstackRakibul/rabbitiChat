import { marked } from 'marked'
import DOMPurify from 'dompurify'

marked.setOptions({ breaks: true, gfm: true })

/**
 * Records the language on the <pre> so the rendered block can grow a header
 * (language + copy + download) without re-parsing the HTML afterwards.
 * marked escapes the code text itself; DOMPurify then sanitizes the result.
 */
const renderer = new marked.Renderer()
const renderCode = renderer.code.bind(renderer)

renderer.code = function code(token, ...rest) {
  const html = renderCode(token, ...rest)
  // marked 12 passes a token object; older shapes pass (code, infostring).
  const lang = (typeof token === 'object' ? token.lang : rest[0]) || ''
  const clean = String(lang).trim().split(/\s+/)[0]
  if (!clean) return html
  return html.replace('<pre>', `<pre data-lang="${clean.replace(/"/g, '')}">`)
}

/** Markdown → sanitized HTML for assistant bubbles. */
export function renderMarkdown(text) {
  // data-lang is ours and carries no behaviour, so it's allowed through the filter.
  return DOMPurify.sanitize(marked.parse(text || '', { renderer }), {
    ADD_ATTR: ['data-lang'],
  })
}
