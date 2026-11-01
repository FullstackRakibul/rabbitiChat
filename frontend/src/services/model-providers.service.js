// Model providers — /api/admin/model-providers, SystemAdmin only.
//
// The key goes up once, in a create or an edit, and never comes back: every answer
// carries only `hasKey` and `apiKeyHint` (its last 4). A blank key on edit keeps the
// stored one. Every rule is the server's; a refusal's HttpError carries
// `{ error, errors }` in `err.data`, keyed by field.

import { http } from './http.js'

const BASE = '/admin/model-providers'

/** Every provider with its status and this month's tokens. */
export function listModelProviders() {
  return http.get(BASE)
}

/** { key, name, baseUrl, apiKey, capabilities, enabled?, streamUsage?, monthlyTokenBudget? } → the provider. */
export function createModelProvider(input) {
  return http.post(BASE, input)
}

/** The same fields; a blank apiKey keeps the stored key, clearApiKey removes it. → the provider. */
export function updateModelProvider(id, input) {
  return http.put(`${BASE}/${id}`, input)
}

export function deleteModelProvider(id) {
  return http.del(`${BASE}/${id}`)
}

/** GET /models, a 5-token reply, then the same streamed. → { ok, status, message, steps[], provider }. */
export function testModelProvider(id) {
  return http.post(`${BASE}/${id}/test`)
}

/** → { models: [{ id, name, inputPrice, outputPrice, kind }], fetchedAt, allowed, toolCapable }. */
export function getModelProviderCatalog(id) {
  return http.get(`${BASE}/${id}/catalog`)
}

export function refreshModelProviderCatalog(id) {
  return http.post(`${BASE}/${id}/catalog/refresh`)
}

/** Saves the shortlist: the models pickers offer, and those of them Chat may send tools. */
export function saveModelProviderModels(id, allowed, toolCapable) {
  return http.put(`${BASE}/${id}/models`, { allowed, toolCapable })
}

export const PROVIDER_CAPABILITIES = [
  { value: 'chat', label: 'Chat' },
  { value: 'embeddings', label: 'Embeddings' },
  { value: 'speech', label: 'Speech (TTS)' },
]

export const PROVIDER_STATUS = {
  ok: { label: 'Connected', tone: 'ok' },
  untested: { label: 'Not tested', tone: 'idle' },
  disabled: { label: 'Switched off', tone: 'idle' },
  key_rejected: { label: 'Key rejected', tone: 'bad' },
  no_credit: { label: 'Credit used up — top up', tone: 'bad' },
  rate_limited: { label: 'Rate limited', tone: 'warn' },
  unreachable: { label: 'Unreachable', tone: 'bad' },
  error: { label: 'Error', tone: 'bad' },
  key_unreadable: { label: 'Key unreadable — paste it again', tone: 'bad' },
}

/** 0.3M, 12.4k, 950. */
export function formatTokens(n) {
  if (n == null) return '—'
  if (n >= 1e6) return `${(n / 1e6).toFixed(n >= 1e7 ? 0 : 1)}M`
  if (n >= 1e3) return `${(n / 1e3).toFixed(n >= 1e4 ? 0 : 1)}k`
  return String(n)
}
