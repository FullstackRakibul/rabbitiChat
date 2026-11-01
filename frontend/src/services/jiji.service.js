// Jiji — /api/jiji (docs/JIJI_PLAN.md, Endpoints).
//
// Every rule is the server's: whose rows these are, what a script may contain,
// what may be approved, when a write is stale. These functions post and fetch;
// nothing here decides. A refusal's HttpError carries the server's body in
// `err.data` ({ error, issues[] }), and a stale write is status 409.

import { http } from './http.js'

/** What is configured: storage, model, voice, speech, YouTube. Names and flags, never a value. */
export function getJijiStatus() {
  return http.get('/jiji/status')
}

/** { contentType, subject, sourceNotes, model?, source? } → { angles: [{ angle, hook, why }], model }. Nothing is saved. */
export function generateJijiIdeas(request) {
  return http.post('/jiji/ideas', request)
}

/** The queue, newest change first. `status` is one of the content statuses, or empty for all. */
export function listJijiContents(status = '') {
  return http.get(`/jiji/contents${status ? `?status=${encodeURIComponent(status)}` : ''}`)
}

/** { contentType, subject, sourceNotes, angle } → the new item (status idea). */
export function createJijiContent(item) {
  return http.post('/jiji/contents', item)
}

/** One item with its script, its jobs and the approval check. */
export function getJijiContent(id) {
  return http.get(`/jiji/contents/${id}`)
}

/** { subject, sourceNotes, angle, expectedUpdatedAt } → the item. */
export function updateJijiContent(id, patch) {
  return http.put(`/jiji/contents/${id}`, patch)
}

export function deleteJijiContent(id) {
  return http.del(`/jiji/contents/${id}`)
}

/** { model?, source?, expectedUpdatedAt } → { content, check }. Replaces the draft. */
export function generateJijiScript(id, request) {
  return http.post(`/jiji/contents/${id}/script/generate`, request)
}

/** → { content, check }. A malformed or rule-breaking script is refused with issues and not saved. */
export function saveJijiScript(id, script, expectedUpdatedAt) {
  return http.put(`/jiji/contents/${id}/script`, { script, expectedUpdatedAt })
}

/** Approves the saved script and queues narration. → the item. */
export function approveJijiScript(id, expectedUpdatedAt) {
  return http.post(`/jiji/contents/${id}/approve-script`, { expectedUpdatedAt })
}

/** Re-queues a Failed job. The server refuses UnknownOutcome. → the item. */
export function retryJijiJob(jobId) {
  return http.post(`/jiji/jobs/${jobId}/retry`)
}

/** Every spoken part's start and end in narration.wav. */
export function getJijiTimings(id) {
  return http.get(`/jiji/contents/${id}/timings`)
}

/**
 * narration.wav as a Blob. An <audio src> cannot send the bearer header, so the file comes
 * through http.js and plays from URL.createObjectURL; the caller revokes the URL.
 */
export async function fetchJijiAudio(id) {
  return (await http.raw(`/jiji/contents/${id}/audio`)).blob()
}

export const JIJI_CONTENT_TYPES = [
  { value: 'TechExplainer', label: 'Tech explainer' },
  { value: 'BuildLog', label: 'Build log' },
  { value: 'BookInsight', label: 'Book insight' },
  { value: 'OriginalThought', label: 'Original thought' },
]

export const JIJI_STATUSES = [
  { value: 'idea', label: 'Idea' },
  { value: 'script_draft', label: 'Script draft' },
  { value: 'script_approved', label: 'Script approved' },
  { value: 'voiced', label: 'Voiced' },
  { value: 'rendered', label: 'Rendered' },
  { value: 'video_approved', label: 'Video approved' },
  { value: 'scheduled', label: 'Scheduled' },
  { value: 'published', label: 'Published' },
]

/** The server's 422/502 issues, or just its message. */
export function jijiIssues(err) {
  return err?.data?.issues?.length ? err.data.issues : []
}
