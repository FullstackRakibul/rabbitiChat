// মানব সম্পদ (HR Assistant) — the HR library's endpoints (docs/HR_MODULE_PROMPT.md §3.2).
// Everything under /api/hr is gated by the `hr` tab on the server; who may change
// the library is decided there too, and reported back as `canEdit`.
import { http } from './http.js'

/**
 * Every library item, plus whether the caller may change the library:
 * { items: [{ id, name, hrType, mode, version, approvedBy, approvedAt, isActive,
 * sortOrder, chars, tokens, chunkCount, embeddedCount, updatedAt, canEdit }],
 * canEdit, types, modes, uploadExtensions }.
 */
export function listHrLibrary() {
  return http.get('/hr/library')
}

/** One item, with its content. */
export function getHrItem(id) {
  return http.get(`/hr/library/${id}`)
}

/** A `search` item's chunks: [{ sourceRef, section, lineFrom, lineTo, chars, tokens, embedded, text }]. */
export function getHrChunks(id) {
  return http.get(`/hr/library/${id}/chunks`)
}

/** Add by pasting text. Mode defaults by type on the server (procedure → search). */
export function createHrItem({ name, hrType, mode, content, approvedBy }) {
  return http.post('/hr/library', { name, hrType, mode, content, approvedBy })
}

/**
 * Add a .md, .txt, .sql or .csv file. The server reads it, honouring a byte-order
 * mark (some procedure exports are UTF-16), and infers name and type from the file.
 */
export function uploadHrFile(file) {
  const form = new FormData()
  form.append('file', file)
  return http.postForm('/hr/library/upload', form)
}

/**
 * Change an item. Only the fields given change; approvedBy '' clears the approval.
 * A content change raises the version and replaces the item's chunks.
 */
export function updateHrItem(id, changes) {
  return http.put(`/hr/library/${id}`, changes)
}

export function deleteHrItem(id) {
  return http.del(`/hr/library/${id}`)
}

/** Retry the chunks the embedding model could not take earlier. */
export function embedHrPending() {
  return http.post('/hr/library/embed', {})
}
