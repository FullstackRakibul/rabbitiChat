// Workspace Studio's own endpoints. Document upload, extraction, confirmation and the
// buyer-profile CRUD already live in document.service.js and are reused from
// there rather than duplicated here.
import { http } from './http.js'

/**
 * The prompt the model will actually be given for this buyer and document —
 * assembled by the same pure builder the extraction path uses, so the preview
 * can never drift from the real run.
 *
 * Pass `numCtxOverride` to see which parts a smaller window would drop without
 * changing the install's setting.
 *
 * Returns { buyer, profileVersion, model, promptHash, messages, report, error }.
 * A document too large for the window answers 422 with the sizes in `report`.
 */
export function previewPrompt({ buyer, documentText, documentId, numCtxOverride } = {}) {
  return http.post('/po-studio/prompt-preview', {
    buyer, documentText, documentId, numCtxOverride,
  })
}

/** Create a buyer profile. Fields beyond name/detectPattern are optional. */
export function createBuyerProfile(profile) {
  return http.post('/buyer-profiles', profile)
}

/**
 * Update a buyer profile. Every save bumps its Version on the server, so the
 * accuracy trend can show whether the edit helped.
 *
 * A worked example that is unconfirmed or in the held-out test set is refused
 * with the reason — the caller shows the server's message rather than guessing.
 */
export function updateBuyerProfile(id, profile) {
  return http.put(`/buyer-profiles/${id}`, profile)
}

export function deleteBuyerProfile(id) {
  return http.del(`/buyer-profiles/${id}`)
}

/** Registered parser keys, so the profile editor offers real choices. */
export function listParsers() {
  return http.get('/buyer-profiles/parsers')
}
