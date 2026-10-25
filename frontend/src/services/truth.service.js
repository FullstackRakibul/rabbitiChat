// সত্য মন্ত্রণালয় · Ministry of Truth — /api/truth (docs/TRUTH_PROMPT.md §8).
//
// Every rule is the server's: the pre-publish checklist, the number check, who
// may publish, what the site text may say. These functions post and fetch.

import { http } from './http.js'

export const listTruthTechs = () => http.get('/truth/techs')
export const createTruthTech = (tech) => http.post('/truth/techs', tech)
export const updateTruthTech = (id, tech) => http.put(`/truth/techs/${id}`, tech)

export const listTruthIssues = () => http.get('/truth/issues')
export const createTruthIssue = (issue) => http.post('/truth/issues', issue)
export const getTruthIssue = (id) => http.get(`/truth/issues/${id}`)
export const updateTruthIssue = (id, issue) => http.put(`/truth/issues/${id}`, issue)
export const deleteTruthIssue = (id) => http.del(`/truth/issues/${id}`)
export const checkTruthIssue = (id) => http.get(`/truth/issues/${id}/check`)
export const previewTruthIssue = (id) => http.get(`/truth/issues/${id}/preview`)
export const withdrawTruthIssue = (id) => http.post(`/truth/issues/${id}/withdraw`)

/**
 * Publishes. A refused checklist answers 400 with { error, problems }; this
 * resolves { ok: false, problems } for it so the editor can show each one.
 */
export async function publishTruthIssue(id) {
  const res = await http.raw(`/truth/issues/${id}/publish`, { method: 'POST' }).catch((e) => e)
  return publishOutcome(res)
}

export const createTruthItem = (issueId, item) => http.post(`/truth/issues/${issueId}/items`, item)
export const updateTruthItem = (id, item) => http.put(`/truth/items/${id}`, item)
export const deleteTruthItem = (id) => http.del(`/truth/items/${id}`)
export const changeTruthStatus = (id, body) => http.post(`/truth/items/${id}/status`, body)
export const addTruthCorrection = (id, body) => http.post(`/truth/items/${id}/corrections`, body)
export const verifyTruthNumber = (id, token, note) => http.post(`/truth/items/${id}/verify`, { token, note })
export const unverifyTruthNumber = (id, token) => http.del(`/truth/items/${id}/verify?token=${encodeURIComponent(token)}`)

// The editable documents — kind is site, memes or glossary (§5, §3A.5, §3A.7).
export const getTruthContent = (kind) => http.get(`/truth/content/${kind}`)
export const saveTruthContentDraft = (kind, text) => http.put(`/truth/content/${kind}/draft`, { text })
export const rollbackTruthContent = (kind, version) => http.post(`/truth/content/${kind}/rollback/${version}`)

/** Publishes a document: { ok: true, version } or { ok: false, problems } (each { field, message }). */
export async function publishTruthContent(kind, text, note) {
  const res = await http.raw(`/truth/content/${kind}/publish`, { method: 'POST', body: { text, note } }).catch((e) => e)
  return publishOutcome(res)
}

/** http.raw throws on any non-2xx; a 400 carrying problems is an answer here, not an error. */
async function publishOutcome(res) {
  if (res instanceof Response) return { ok: true, ...(await res.json()) }
  if (res?.status === 400) return { ok: false, error: res.message, problems: res.data?.problems ?? [] }
  throw res
}

// ─────────── fix guides (§6) ───────────
export const listTruthPlatforms = () => http.get('/truth/platforms')
export const listTruthGuides = () => http.get('/truth/guides')
export const createTruthGuide = (guide) => http.post('/truth/guides', guide)
export const getTruthGuide = (id) => http.get(`/truth/guides/${id}`)
export const updateTruthGuide = (id, guide) => http.put(`/truth/guides/${id}`, guide)
export const deleteTruthGuide = (id) => http.del(`/truth/guides/${id}`)
/** Every variant rendered and checked — and the server records that the grid was opened (§6.3). */
export const openTruthGuideGrid = (id) => http.post(`/truth/guides/${id}/grid`)
export const checkTruthGuide = (id) => http.get(`/truth/guides/${id}/check`)
export const ackTruthFinding = (id, findingKey, note) => http.post(`/truth/guides/${id}/ack`, { findingKey, note })
export const setTruthGuideChecklist = (id, checklist) => http.put(`/truth/guides/${id}/checklist`, checklist)
export const withdrawTruthGuide = (id) => http.post(`/truth/guides/${id}/withdraw`)
export const addTruthGuideCorrection = (id, body) => http.post(`/truth/guides/${id}/corrections`, body)
export const verifyTruthGuideNumber = (id, token, note) => http.post(`/truth/guides/${id}/verify`, { token, note })

export async function publishTruthGuide(id) {
  const res = await http.raw(`/truth/guides/${id}/publish`, { method: 'POST' }).catch((e) => e)
  return publishOutcome(res)
}
