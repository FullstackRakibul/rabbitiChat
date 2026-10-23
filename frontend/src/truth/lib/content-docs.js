// The memes (§3A.5) and the glossary (§3A.7): built-in defaults, the admin's
// published override, and the rules both are held to — read from their schemas,
// the same rules backend/Truth/TruthContent.cs applies on publish.

import { checkString } from './site-text.js'

/** The override when it has the right shape, else the default: memes and entries are replaced as a whole list. */
export function resolveDoc(defaults, override, listKey) {
  return Array.isArray(override?.[listKey]) ? override[listKey] : defaults[listKey]
}

const plain = (v, max) => checkString(v, { maxLength: max, required: true }).map((m) => (m === 'Bangla text is required' ? 'text is required' : m))

/** Problems of a memes document: [{ path, message }]. */
export function validateMemes(doc, schema) {
  const errors = []
  const add = (path, message) => errors.push({ path, message })
  const def = schema.$defs.meme
  const p = def.properties
  if (!doc || typeof doc !== 'object' || !Array.isArray(doc.memes)) return [{ path: '', message: 'must be { "memes": [ … ] }' }]
  for (const k of Object.keys(doc)) if (k !== 'memes') add(k, 'unknown key')
  if (doc.memes.length > schema.properties.memes.maxItems) add('memes', `at most ${schema.properties.memes.maxItems} memes`)
  const ids = new Set()
  doc.memes.forEach((m, i) => {
    const at = `memes[${i}]`
    if (!m || typeof m !== 'object') return add(at, 'must be an object')
    for (const k of Object.keys(m)) if (!(k in p)) add(`${at}.${k}`, 'unknown key')
    for (const k of def.required) if (!(k in m)) add(`${at}.${k}`, 'missing key')
    if (typeof m.id !== 'string' || !new RegExp(p.id.pattern).test(m.id)) add(`${at}.id`, 'lower-case letters, digits and dashes')
    else if (ids.has(m.id)) add(`${at}.id`, `"${m.id}" is used twice`)
    else ids.add(m.id)
    for (const lang of ['bn', 'en']) {
      if (!(lang in m)) continue
      for (const msg of plain(m[lang], p[lang].maxLength)) add(`${at}.${lang}`, msg)
      if (typeof m[lang] === 'string' && m[lang].split('\n').length > p[lang]['x-maxLines']) add(`${at}.${lang}`, `at most ${p[lang]['x-maxLines']} lines`)
    }
    for (const k of ['mood', 'style']) if (k in m && !p[k].enum.includes(m[k])) add(`${at}.${k}`, `one of: ${p[k].enum.join(', ')}`)
    if ('pose' in m && !p.pose.enum.includes(m.pose ?? null)) add(`${at}.pose`, `one of: ${p.pose.enum.map((x) => x ?? 'null').join(', ')}`)
    if ('placement' in m) {
      const allowed = p.placement.items.enum
      if (!Array.isArray(m.placement) || !m.placement.length) add(`${at}.placement`, 'at least one placement')
      else for (const s of m.placement) {
        if (!allowed.includes(s)) add(`${at}.placement`, `unknown placement "${s}"; one of: ${allowed.join(', ')}`)
        else if (m.placement.filter((x) => x === s).length > 1) add(`${at}.placement`, `"${s}" twice`)
      }
    }
    if ('weight' in m && !(Number.isInteger(m.weight) && m.weight >= 1 && m.weight <= 10)) add(`${at}.weight`, 'a whole number 1–10')
    if ('active' in m && typeof m.active !== 'boolean') add(`${at}.active`, 'true or false')
  })
  return dedupe(errors)
}

/** Problems of a glossary document: [{ path, message }]. */
export function validateGlossary(doc, schema) {
  const errors = []
  const add = (path, message) => errors.push({ path, message })
  const p = schema.$defs.entry.properties
  if (!doc || typeof doc !== 'object' || !Array.isArray(doc.entries)) return [{ path: '', message: 'must be { "entries": [ … ] }' }]
  for (const k of Object.keys(doc)) if (k !== 'entries') add(k, 'unknown key')
  const ids = new Set()
  doc.entries.forEach((e, i) => {
    const at = `entries[${i}]`
    if (!e || typeof e !== 'object') return add(at, 'must be an object')
    for (const k of Object.keys(e)) if (!(k in p)) add(`${at}.${k}`, 'unknown key')
    if (typeof e.id !== 'string' || !new RegExp(p.id.pattern).test(e.id)) add(`${at}.id`, 'lower-case letters, digits and dashes')
    else if (ids.has(e.id)) add(`${at}.id`, `"${e.id}" is used twice`)
    else ids.add(e.id)
    for (const part of ['term', 'def']) {
      if (!e[part] || typeof e[part] !== 'object') { add(`${at}.${part}`, 'needs bn and en'); continue }
      for (const k of Object.keys(e[part])) if (!['bn', 'en'].includes(k)) add(`${at}.${part}.${k}`, 'unknown key')
      for (const lang of ['bn', 'en']) for (const msg of plain(e[part][lang], p[part]['x-maxLength'])) add(`${at}.${part}.${lang}`, msg)
    }
    const m = p.match
    if (!Array.isArray(e.match) || e.match.length < m.minItems || e.match.length > m.maxItems) add(`${at}.match`, `${m.minItems}–${m.maxItems} words to underline`)
    else if (e.match.some((w) => typeof w !== 'string' || w.trim().length < 2 || w.length > 30)) add(`${at}.match`, 'each word 2–30 characters')
  })
  return dedupe(errors)
}

function dedupe(errors) {
  const seen = new Set()
  return errors.filter((e) => { const k = `${e.path} ${e.message}`; return seen.has(k) ? false : seen.add(k) })
}
