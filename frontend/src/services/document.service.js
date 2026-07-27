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
 * Extract structured PO JSON from an ingested document.
 * Returns { documentId, fileName, buyer, extracted, sources }.
 */
export function extractDocument(id, model, signal) {
  return http.post(`/documents/${id}/extract`, { model }, { signal })
}

/**
 * Stateless RAG workspace chat (never saved to Conversations).
 * Streams the reply via onChunk. Pin documentId to ground on one PO.
 */
export function workspaceChat({ documentId, model, message }, onChunk, signal) {
  return http.stream('/documents/workspace-chat', { documentId, model, message }, onChunk, signal)
}
