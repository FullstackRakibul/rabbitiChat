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
 * @returns {Promise<Array<{name, source, contextLength, capabilities, metered, sourceLabel}>>}
 */
export function listModels() {
  return http.get('/models')
}

/** True when this model can be used for tool calling (agentic search). */
export function supportsTools(model) {
  return (model?.capabilities || []).some((c) => String(c).toLowerCase() === 'tools')
}

/**
 * The same list arranged for a picker: local first (the private default), then
 * Ollama cloud, then any configured OpenAI-compatible provider in config order.
 * @returns {Promise<Array<{key: string, label: string, models: Array}>>}
 */
export async function listModelsGrouped() {
  const models = await listModels()
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

  return groups
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
