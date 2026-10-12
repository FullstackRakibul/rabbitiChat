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
export function listModels() {
  return http.get('/models')
}

/**
 * The same list plus whether the role is held to a single model.
 *
 * `locked` rides in the X-Model-Locked header rather than the body, so the
 * plain listModels() shape stays exactly as every existing caller expects.
 * @returns {Promise<{models: Array, locked: boolean}>}
 */
export async function listModelsWithPolicy() {
  const res = await http.raw('/models')
  const models = await res.json()
  return { models, locked: res.headers.get('X-Model-Locked') === 'true' }
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
export async function listModelsGrouped() {
  const { models, locked } = await listModelsWithPolicy()
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
  return { groups, locked }
}

/** Remembers the last picked model across reloads. Shape: { source, model }. */
export function saveModelChoice(source, model) {
  try {
    localStorage.setItem(LAST_CHOICE_KEY, JSON.stringify({ source, model }))
  } catch { /* private mode / storage disabled — the picker still works */ }
}

export function loadModelChoice() {
  try {
    const raw = localStorage.getItem(LAST_CHOICE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw)
    return parsed && parsed.model ? parsed : null
  } catch {
    return null
  }
}
