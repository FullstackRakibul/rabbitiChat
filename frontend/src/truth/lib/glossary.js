// The Newspeak Dictionary (§3A.7): the entries in use, finding their words in a
// summary, and the one sheet that explains a tapped term. Plain text all through.

import { ref } from 'vue'
import defaultGlossary from '../content/glossary.default.json'
import { resolveDoc } from './content-docs.js'

export const entries = ref(defaultGlossary.entries)
export function applyGlossary(override) { entries.value = resolveDoc(defaultGlossary, override, 'entries') }

/** The entry the sheet shows, or null. */
export const openEntry = ref(null)
let returnFocus = null
export function openTerm(entry, from) { returnFocus = from ?? null; openEntry.value = entry }
export function closeTerm() { openEntry.value = null; returnFocus?.focus?.(); returnFocus = null }

const escape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/**
 * One pattern over every entry's words, longest first. A word starts where no
 * letter or digit is before it; a Latin word also ends where none follows, while
 * a Bangla one may take a suffix ("প্যাচের" is "প্যাচ").
 */
function pattern(list) {
  const words = list.flatMap((e) => e.match.map((w) => [w, e])).sort((a, b) => b[0].length - a[0].length)
  if (!words.length) return null
  const alt = words.map(([w]) => `${escape(w)}${/[A-Za-z0-9]$/.test(w) ? '(?![\\p{L}\\p{N}])' : ''}`).join('|')
  return { re: new RegExp(`(?<![\\p{L}\\p{M}\\p{N}])(?:${alt})`, 'giu'), words }
}

/**
 * A text in parts: { text } as it is, and { text, entry } for the first time each
 * entry's word appears (`seen` carries that across the parts of one summary).
 */
export function splitTerms(text, list, seen = new Set()) {
  const p = pattern(list)
  if (!p || !text) return [{ text }]
  const out = []
  let last = 0
  for (const m of text.matchAll(p.re)) {
    const lower = m[0].toLowerCase()
    const entry = p.words.find(([w]) => w.toLowerCase() === lower)?.[1]
    if (!entry || seen.has(entry.id)) continue
    seen.add(entry.id)
    if (m.index > last) out.push({ text: text.slice(last, m.index) })
    out.push({ text: m[0], entry })
    last = m.index + m[0].length
  }
  if (last < text.length) out.push({ text: text.slice(last) })
  return out
}
