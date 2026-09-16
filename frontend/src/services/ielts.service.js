// IELTS — Reading engine + the mistake spine.
//
// Everything goes through http.js so the bearer token and AuthError handling
// stay in one place. Every endpoint is owner-scoped server-side: this file never
// passes a user id, because the server takes it from the token rather than
// trusting the client.

import { http } from './http.js'

// ---- Mistakes (Phase 0) ----

/**
 * The mistake log. All filters optional.
 * @param {{module?: string, type?: string, due?: boolean}} filters
 * @returns {Promise<Array>} due first, then most recently seen
 */
export function listMistakes({ module, type, due } = {}) {
  const q = new URLSearchParams()
  if (module) q.set('module', module)
  if (type) q.set('type', type)
  if (due) q.set('due', 'true')

  const qs = q.toString()
  return http.get(`/ielts/mistakes${qs ? `?${qs}` : ''}`)
}

/** Counts by question type, for the weakness bars. */
export function mistakeSummary(module) {
  return http.get(`/ielts/mistakes/summary${module ? `?module=${encodeURIComponent(module)}` : ''}`)
}

/**
 * Records a review: pushes the next review date out (2d → 5d → 11d).
 * @param {number} id
 * @param {boolean} [resolved] true closes the row so it stops resurfacing
 */
export function markMistakeReviewed(id, resolved) {
  return http.post(`/ielts/mistakes/${id}/reviewed`, { resolved })
}

/**
 * The user's own diagnosis of why it went wrong. Nothing guesses this —
 * only a person can say whether it was an inference error or a keyword trap.
 */
export function tagMistake(id, { mistakeType, severity } = {}) {
  return http.post(`/ielts/mistakes/${id}/tag`, { mistakeType, severity })
}

// ---- Reading: authoring (Phase 1) ----

/** The user's own test library: [{ id, title, source, questionCount, createdAt }] */
export function listTests() {
  return http.get('/ielts/reading/tests')
}

/** One test with its questions AND answer keys — the authoring view. */
export function getTest(id) {
  return http.get(`/ielts/reading/tests/${id}`)
}

export function createTest({ title, passage, source } = {}) {
  return http.post('/ielts/reading/tests', { title, passage, source })
}

export function updateTest(id, { title, passage, source } = {}) {
  return http.put(`/ielts/reading/tests/${id}`, { title, passage, source })
}

export function deleteTest(id) {
  return http.del(`/ielts/reading/tests/${id}`)
}

/** The formats scoring can handle — served from the same constant it validates against. */
export function questionTypes() {
  return http.get('/ielts/reading/question-types')
}

/**
 * @param {number} testId
 * @param {{questionType, prompt, options?, correctAnswer, skill?, topic?, difficulty?, order?}} q
 *   correctAnswer may list acceptable variants pipe-separated ("carbon dioxide|CO2").
 */
export function addQuestion(testId, q) {
  return http.post(`/ielts/reading/tests/${testId}/questions`, q)
}

export function updateQuestion(id, q) {
  return http.put(`/ielts/reading/questions/${id}`, q)
}

export function deleteQuestion(id) {
  return http.del(`/ielts/reading/questions/${id}`)
}

// ---- Reading: sitting a test ----

/**
 * Opens a sitting. The questions come back WITHOUT their answer keys — the
 * server projects to a shape that has no field for them.
 * @returns {Promise<{attemptId, testId, title, passage, startedAt, questions}>}
 */
export function startAttempt(testId) {
  return http.post(`/ielts/reading/tests/${testId}/attempts`)
}

/**
 * Scores the sitting. Deterministic — a string comparison per question, no model
 * involved — so the same answers always produce the same band.
 * @param {number} attemptId
 * @param {Array<{questionId: number, given: string}>} answers
 * @param {number} [durationSeconds] measured by the client's timer
 */
export function submitAttempt(attemptId, answers, durationSeconds) {
  return http.post(`/ielts/reading/attempts/${attemptId}/submit`, { answers, durationSeconds })
}

/** Attempt history: score, band and date per sitting. */
export function listAttempts() {
  return http.get('/ielts/reading/attempts')
}

/** Full review: your answer vs the key, per question, with type/skill/topic. */
export function getAttempt(id) {
  return http.get(`/ielts/reading/attempts/${id}`)
}
