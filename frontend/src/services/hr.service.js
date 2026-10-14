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

/** Add one or many files in one request; each is answered on its own: { results: [{ file, ok, item, error }] }. */
export function uploadHrFiles(files, fields = {}) {
  const form = new FormData()
  for (const f of files) form.append('file', f)
  for (const [k, v] of Object.entries(fields)) if (v) form.append(k, v)
  return http.postForm('/hr/library/upload', form)
}

/** Queue an item whose embedding failed again. */
export function retryHrItem(id) {
  return http.post(`/hr/library/${id}/retry`, {})
}

// ───────────────────────── Ask ─────────────────────────

/**
 * Instant hybrid search, no model: the top 10 chunks with SourceRef, item name
 * and a snippet whose matches are wrapped in « ». `items` limits it to those ids.
 */
export function searchHr(q, items = []) {
  const scope = items.length ? `&items=${items.join(',')}` : ''
  return http.get(`/hr/search?q=${encodeURIComponent(q)}${scope}`)
}

export function listHrConversations() {
  return http.get('/hr/conversations')
}

/** One HR conversation: { id, title, messages: [{ role, content, meta: { context, calculations, unverified } }] }. */
export function getHrConversation(id) {
  return http.get(`/hr/conversations/${id}`)
}

export function deleteHrConversation(id) {
  return http.del(`/hr/conversations/${id}`)
}

/**
 * Ask the HR assistant; the answer streams. Frames: `context` (what was sent),
 * `content`, `tool` (calculate), `error`, `unverified` (amounts the number check
 * could not trace), `done`. `items` is the "Search in" scope; empty means the
 * whole library. Resolves with the conversation id.
 */
export async function streamHrChat({ conversationId, message, items = [], model }, handlers, signal) {
  let buffer = ''
  const consume = (line) => {
    const trimmed = line.trim()
    if (!trimmed) return
    let frame
    try { frame = JSON.parse(trimmed) } catch { return }
    switch (frame.type) {
      case 'context': handlers.onContext?.(frame); break
      case 'content': handlers.onContent?.(frame.text ?? ''); break
      case 'tool': handlers.onTool?.(frame); break
      case 'error': handlers.onError?.(frame.message ?? 'The model failed.'); break
      case 'unverified': handlers.onUnverified?.(frame.amounts ?? []); break
    }
  }
  const res = await http.stream('/hr/chat', { conversationId, message, items, model }, (chunk) => {
    buffer += chunk
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    lines.forEach(consume)
  }, signal)
  consume(buffer)
  return Number(res.headers.get('X-Conversation-Id')) || conversationId
}
