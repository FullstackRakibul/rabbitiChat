// Jiji — /api/jiji (docs/JIJI_PLAN.md, Endpoints).
//
// Every rule is the server's: whose rows these are, what may be approved, when a
// job may be retried. These functions post and fetch; nothing here decides.

import { http } from './http.js'

/** What is configured: storage, model, speech, YouTube. Names and flags, never a value. */
export function getJijiStatus() {
  return http.get('/jiji/status')
}

/** The queue, newest change first. `status` is one of the content statuses, or empty for all. */
export function listJijiContents(status = '') {
  return http.get(`/jiji/contents${status ? `?status=${encodeURIComponent(status)}` : ''}`)
}

/** One item with its script and its jobs. */
export function getJijiContent(id) {
  return http.get(`/jiji/contents/${id}`)
}
