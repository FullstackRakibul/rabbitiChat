import { http } from './http.js'

/**
 * Upload a PO / order file for ingestion (extract → chunk → embed → index).
 * Returns { id, fileName, buyer, status, chunks }.
 */
export function uploadDocument(file, buyer, signal) {
  const form = new FormData()
  form.append('file', file)
  if (buyer) form.append('buyer', buyer)
  return http.postForm('/documents', form, { signal })
}

/** The current user's documents, newest first. */
export function listDocuments() {
  return http.get('/documents')
}

export function deleteDocument(id) {
  return http.del(`/documents/${id}`)
}

/**
 * Extract structured PO data through the detect → parse → validate pipeline.
 * Returns { runId, status, parser, buyer, detectConfidence, note, error,
 *           failedChecks, checks, data }. status "ok" only comes from a
 * deterministic parser with every check passing.
 */
export function extractDocument(id, { model, buyerProfileId } = {}, signal) {
  const q = buyerProfileId ? `?buyerProfileId=${buyerProfileId}` : ''
  return http.post(`/documents/${id}/extract${q}`, { model }, { signal })
}

/** The stored original as a blob (auth header goes with it — no cookie). */
export async function fetchDocumentFile(id) {
  const res = await http.raw(`/documents/${id}/file`)
  return res.blob()
}

/** Human sign-off: the edited payload plus which field paths changed. */
export function confirmExtraction(id, resultJson, edits) {
  return http.post(`/documents/${id}/extraction/confirm`, { resultJson, edits })
}

/** Buyer profiles for the extract-time selector ("Auto-detect" = omit the id). */
export function listBuyerProfiles() {
  return http.get('/buyer-profiles')
}

/**
 * The stored extraction for a document, or null when it hasn't been extracted yet.
 * Avoids re-running the model just to re-display a result.
 */
export async function getExtraction(id) {
  const res = await http.raw(`/documents/${id}/extract`)
  return res.status === 204 ? null : res.json()
}

/**
 * Stateless RAG workspace chat (never saved to Conversations).
 * Streams the reply via onChunk. Pin documentId to ground on one PO.
 */
export function workspaceChat({ documentId, model, message }, onChunk, signal) {
  return http.stream('/documents/workspace-chat', { documentId, model, message }, onChunk, signal)
}
