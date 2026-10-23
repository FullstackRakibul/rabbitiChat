// The global site text (docs/TRUTH_PROMPT.md §5): merge, format and validate.
//
// The built-in default (content/site.default.json) is bundled and always works.
// An administrator's published override is merged over it at load, key by key,
// so a change reaches every visitor without a redeploy. Text is plain text with
// {name} placeholders: it is never parsed as HTML or as any message syntax, and
// backend/Truth/TruthText.cs applies exactly the same rules for the share pages
// and the emails — a key reads the same in all three places (lib/i18n.js is the page's).

const PLACEHOLDER = /\{([a-zA-Z][a-zA-Z0-9]*)\}/g
const HTML = /<\s*[a-zA-Z!/?]|&(#[0-9]+|#x[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);/
const SCHEME = /\b([a-zA-Z][a-zA-Z0-9+.-]*):\/\//g
const SCRIPT_SCHEME = /\b(javascript|data|vbscript):/i
// Control characters other than a line break.
const CONTROL = /[\u0000-\u0009\u000B-\u001F\u007F]/

/** The value at a dotted key, or undefined. */
export function lookup(doc, key) {
  let node = doc
  for (const part of key.split('.')) {
    if (node === null || typeof node !== 'object' || !Object.hasOwn(node, part)) return undefined
    node = node[part]
  }
  return node
}

/**
 * The default with the override's non-empty strings laid over it. Only keys the
 * default has are taken, and only strings — so an override from an older or newer
 * schema can neither add text nor break the shape the components expect.
 */
export function mergeText(base, override) {
  if (base === null || typeof base !== 'object') return base
  const out = {}
  for (const [k, v] of Object.entries(base)) {
    const o = override !== null && typeof override === 'object' ? override[k] : undefined
    if (typeof v === 'string') out[k] = typeof o === 'string' && o.trim() ? o : v
    else out[k] = mergeText(v, o)
  }
  return out
}

/** Replaces {name} with params[name]; a placeholder without a value is left as written. */
export function formatText(text, params = {}) {
  return String(text).replace(PLACEHOLDER, (m, name) => (Object.hasOwn(params, name) ? String(params[name]) : m))
}

/** Splits a text into literal parts and placeholder names (Msg.js renders them in parts). */
export function parseText(text) {
  const parts = []
  let last = 0
  for (const m of String(text).matchAll(PLACEHOLDER)) {
    if (m.index > last) parts.push(text.slice(last, m.index))
    parts.push({ name: m[1] })
    last = m.index + m[0].length
  }
  if (last < text.length) parts.push(text.slice(last))
  return parts
}

/** Every string key of a schema's language definition, dotted, in order, with its schema node. */
export function schemaKeys(schema) {
  const out = []
  const walk = (node, prefix) => {
    for (const [k, v] of Object.entries(node.properties ?? {})) {
      const key = prefix ? `${prefix}.${k}` : k
      if (v.type === 'object') walk(v, key)
      else out.push({ key, placeholders: v['x-placeholders'] ?? [], maxLength: v.maxLength ?? 600 })
    }
  }
  walk(schema.$defs.lang, '')
  return out
}

/** The problems of one string, as messages. `required`: Bangla must not be empty. */
export function checkString(value, { placeholders = [], maxLength = 600, required = false } = {}) {
  const errors = []
  if (typeof value !== 'string') return ['must be text']
  if (required && !value.trim()) errors.push('Bangla text is required')
  if (value.length > maxLength) errors.push(`longer than ${maxLength} characters`)
  if (HTML.test(value)) errors.push('HTML is not allowed')
  if (CONTROL.test(value)) errors.push('control characters are not allowed')
  if (SCRIPT_SCHEME.test(value)) errors.push('only https links are allowed')
  for (const m of value.matchAll(SCHEME)) {
    if (m[1].toLowerCase() !== 'https') { errors.push('only https links are allowed'); break }
  }
  const allowed = new Set(placeholders)
  for (const m of value.matchAll(PLACEHOLDER)) {
    if (!allowed.has(m[1]))
      errors.push(`unknown placeholder {${m[1]}}${placeholders.length ? `; allowed: ${placeholders.map((p) => `{${p}}`).join(' ')}` : '; this text takes none'}`)
  }
  if (/[{}]/.test(value.replace(PLACEHOLDER, ''))) errors.push('a { or } that is not a placeholder')
  return [...new Set(errors)]
}

/**
 * Validates a whole override document against the schema: [{ path, message }].
 * Unknown keys, missing keys, wrong types, unknown placeholders, HTML and
 * non-https links are refused; Bangla must be filled in, English may be empty.
 */
export function validateText(doc, schema) {
  const errors = []
  if (doc === null || typeof doc !== 'object' || Array.isArray(doc)) return [{ path: '', message: 'must be an object with bn and en' }]
  for (const k of Object.keys(doc)) if (!['bn', 'en'].includes(k)) errors.push({ path: k, message: 'unknown key' })
  for (const lang of ['bn', 'en']) {
    if (!Object.hasOwn(doc, lang)) { errors.push({ path: lang, message: 'missing key' }); continue }
    const walk = (node, value, path) => {
      if (node.type === 'object') {
        if (value === null || typeof value !== 'object' || Array.isArray(value)) { errors.push({ path, message: 'must be an object' }); return }
        for (const k of Object.keys(value)) if (!Object.hasOwn(node.properties, k)) errors.push({ path: `${path}.${k}`, message: 'unknown key' })
        for (const [k, child] of Object.entries(node.properties)) {
          if (!Object.hasOwn(value, k)) errors.push({ path: `${path}.${k}`, message: 'missing key' })
          else walk(child, value[k], `${path}.${k}`)
        }
        return
      }
      for (const message of checkString(value, {
        placeholders: node['x-placeholders'] ?? [], maxLength: node.maxLength ?? 600, required: lang === 'bn',
      })) errors.push({ path, message })
    }
    walk(schema.$defs.lang, doc[lang], lang)
  }
  return errors
}
