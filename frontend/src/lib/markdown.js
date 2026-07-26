import { marked } from 'marked'
import DOMPurify from 'dompurify'

marked.setOptions({ breaks: true, gfm: true })

/** Markdown → sanitized HTML for assistant bubbles. */
export function renderMarkdown(text) {
  return DOMPurify.sanitize(marked.parse(text || ''))
}
