// Model catalogue. The merged list comes from the backend, which asks every
// registered source; the browser never talks to Ollama (local or cloud) itself,
// and never sees the cloud key.
import { http } from './http.js'

const LAST_CHOICE_KEY = 'rabbitic.model'

/**
 * Labels for the two built-in sources. Configured providers (OpenRouter, Groq, …)
 * send their own `sourceLabel`, so adding one needs no edit here.
 */
const SOURCE_LABELS = {
  local: 'Local (private)',
  cloud: 'Ollama Cloud (metered)',
}

/** Group heading: the server's label wins, then a known built-in, then the raw key. */
function labelFor(model, key) {
  return model?.sourceLabel || SOURCE_LABELS[key] || key
}

/**
 * Every model the server can serve.
 *
 * For a role held to a list, the server has already filtered this down; the
 * browser never sees a model it may not run. That filtering is convenience,
 * not the control - every endpoint enforces the policy itself.
 * @returns {Promise<Array<{name, source, contextLength, capabilities, metered, sourceLabel}>>}
 */
export function listModels(module = '') {
  return http.get(modelsPath(module))
}

/**
 * `module` is chat, hr or jiji: the server then answers with Access → Models' rules
 * for that module (and X-Model-Locked when the module is locked for this user).
 * Without it, the per-role model policy every other picker uses.
 */
function modelsPath(module) {
  return module ? `/models?module=${encodeURIComponent(module)}` : '/models'
}

/**
 * The same list plus whether the role is held to a single model.
 *
 * `locked` rides in the X-Model-Locked header rather than the body, so the
 * plain listModels() shape stays exactly as every existing caller expects.
 * @returns {Promise<{models: Array, locked: boolean}>}
 */
export async function listModelsWithPolicy(module = '') {
  const res = await http.raw(modelsPath(module))
  const models = await res.json()
  // "source|model", URL-encoded: the module's default, for a picker with no remembered choice.
  const raw = res.headers.get('X-Model-Default')
  const [s, m] = raw ? raw.split('|').map(decodeURIComponent) : []
  return { models, locked: res.headers.get('X-Model-Locked') === 'true', defaultChoice: m ? { source: s, model: m } : null }
}

/** True when this model can be used for tool calling (agentic search). */
export function supportsTools(model) {
  return (model?.capabilities || []).some((c) => String(c).toLowerCase() === 'tools')
}

/**
 * The same list arranged for a picker: local first (the private default), then
 * Ollama cloud, then any configured OpenAI-compatible provider in config order.
 * @returns {Promise<{groups: Array<{key: string, label: string, models: Array}>, locked: boolean}>}
 */
export async function listModelsGrouped(module = '') {
  const { models, locked, defaultChoice } = await listModelsWithPolicy(module)
  const order = ['local', 'cloud']

  const groups = []
  for (const key of order) {
    const items = models.filter((m) => m.source === key)
    if (items.length) groups.push({ key, label: labelFor(items[0], key), models: items })
  }

  // A source the backend adds later still shows up, without an edit here.
  for (const m of models) {
    if (order.includes(m.source)) continue
    let g = groups.find((x) => x.key === m.source)
    if (!g) { g = { key: m.source, label: labelFor(m, m.source), models: [] }; groups.push(g) }
    g.models.push(m)
  }

  // Returned together, from the one request: a second call for `locked` could
  // answer differently and leave the picker disagreeing with its own list.
  return { groups, locked, defaultChoice }
}

/**
 * Remembers the last picked model across reloads, per module: HR's choice must not
 * become Chat's. Shape: { source, model }.
 */
export function saveModelChoice(source, model, module = '') {
  try {
    localStorage.setItem(choiceKey(module), JSON.stringify({ source, model }))
  } catch { /* private mode / storage disabled — the picker still works */ }
}

const choiceKey = (module) => (module ? `${LAST_CHOICE_KEY}.${module}` : LAST_CHOICE_KEY)

/** "0.2x" from 0.2; '' when the model has no multiplier (Ollama). */
export function formatMultiplier(m) {
  if (m == null) return ''
  return `${Number.isInteger(m) ? m.toFixed(1) : String(m)}x`
}

export function loadModelChoice(module = '') {
  try {
    const raw = localStorage.getItem(choiceKey(module))
    if (!raw) return null
    const parsed = JSON.parse(raw)
    return parsed && parsed.model ? parsed : null
  } catch {
    return null
  }
}
