// The fix guides' templated commands (docs/TRUTH_PROMPT.md §6.2): a tiny,
// logic-limited renderer, written here, with no eval.
//
//   {{php}}                         the variable's value
//   {{#if php>=8.5}} … {{/if}}      a condition; {{else}} inside is allowed; ifs nest
//   conditions                      name · !name · name OP value, joined by && (binds first) and ||
//                                   OP: == != >= <= > <   value: bare or "quoted"
//
// Comparisons are by version when both sides are numbers with dots (8.10 > 8.9),
// otherwise only == and != apply, as text. A line holding nothing but an if,
// else or /if tag disappears whole, so a conditional line leaves no blank line.
// The output is plain text: nothing here is HTML, and the page shows it as text.
// backend/Truth/GuideTemplate.cs is the same renderer, case for case
// (scripts/truth/template-cases.json).

const TAG = /\{\{\s*(#if\s+[^}]*?|else|\/if|[a-zA-Z][\w-]*)\s*\}\}/g
const STANDALONE = /^[ \t]*\{\{\s*(#if\s+[^}]*?|else|\/if)\s*\}\}[ \t]*$/
const ATOM = /^\s*(!)?\s*([a-zA-Z][\w-]*)\s*(?:(==|!=|>=|<=|>|<)\s*("[^"]*"|[^\s"]+))?\s*$/
const VERSION = /^\d+(\.\d+)*$/

export function compareVersions(a, b) {
  const x = String(a).split('.').map(Number), y = String(b).split('.').map(Number)
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] ?? 0) - (y[i] ?? 0)
    if (d) return d < 0 ? -1 : 1
  }
  return 0
}

const truthy = (v) => v !== undefined && v !== null && v !== '' && !['false', '0', 'no'].includes(String(v).toLowerCase())

/** Evaluates one condition against the values; problems go to `errors`. */
export function evalCondition(cond, values, errors = []) {
  return cond.split('||').some((or) => or.split('&&').every((part) => {
    const m = ATOM.exec(part)
    if (!m) { errors.push(`bad condition "${part.trim()}"`); return false }
    const [, not, name, op, raw] = m
    if (!Object.hasOwn(values, name)) { errors.push(`unknown variable "${name}"`); return false }
    const v = String(values[name])
    if (!op) return not ? !truthy(v) : truthy(v)
    if (not) { errors.push(`"!" with a comparison in "${part.trim()}"`); return false }
    const w = raw.startsWith('"') ? raw.slice(1, -1) : raw
    if (VERSION.test(v) && VERSION.test(w)) {
      const c = compareVersions(v, w)
      return { '==': c === 0, '!=': c !== 0, '>=': c >= 0, '<=': c <= 0, '>': c > 0, '<': c < 0 }[op]
    }
    if (op === '==') return v === w
    if (op === '!=') return v !== w
    errors.push(`"${op}" needs two versions in "${part.trim()}"`)
    return false
  }))
}

/** { text, errors }. Unknown placeholders stay as written, and are reported. */
export function renderTemplate(template, values = {}) {
  const errors = []
  const tokens = []
  // A line with only a block tag becomes just the tag: no text, no line break.
  for (const line of String(template ?? '').split(/(?<=\n)/)) {
    const body = line.replace(/\r?\n$/, '')
    if (STANDALONE.test(body)) { tokens.push({ tag: STANDALONE.exec(body)[1].trim() }); continue }
    let last = 0
    for (const m of line.matchAll(TAG)) {
      if (m.index > last) tokens.push({ text: line.slice(last, m.index) })
      tokens.push({ tag: m[1].trim() })
      last = m.index + m[0].length
    }
    if (last < line.length) tokens.push({ text: line.slice(last) })
  }

  let out = ''
  const stack = []                  // { on: this branch prints, parent: the enclosing one printed, elsed }
  const printing = () => stack.every((s) => s.on)
  for (const t of tokens) {
    if (t.text !== undefined) { if (printing()) out += t.text; continue }
    if (t.tag.startsWith('#if')) {
      const parent = printing()
      stack.push({ on: parent && evalCondition(t.tag.slice(3).trim(), values, parent ? errors : []), parent, elsed: false })
    } else if (t.tag === 'else') {
      const top = stack.at(-1)
      if (!top || top.elsed) { errors.push('{{else}} without its {{#if}}'); continue }
      top.elsed = true
      top.on = top.parent && !top.on
    } else if (t.tag === '/if') {
      if (!stack.length) errors.push('{{/if}} without its {{#if}}')
      else stack.pop()
    } else if (printing()) {
      if (Object.hasOwn(values, t.tag)) out += String(values[t.tag])
      else { errors.push(`unknown variable "${t.tag}"`); out += `{{${t.tag}}}` }
    }
  }
  if (stack.length) errors.push(`${stack.length} {{#if}} not closed`)
  return { text: out, errors: [...new Set(errors)] }
}

/** Whether a step shows for these values: its platform (or "any") and its condition. */
export function stepVisible(step, values) {
  if (step.platform && step.platform !== 'any' && step.platform !== values.platform) return false
  return !step.condition || evalCondition(step.condition, values)
}
